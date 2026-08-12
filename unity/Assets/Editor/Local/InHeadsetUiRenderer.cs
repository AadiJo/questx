using System;
using System.IO;
using System.Linq;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;
using UnityCamera = UnityEngine.Camera;

namespace QuestX.EditorTools
{
    /// <summary>
    /// Renders the real world-space QuestX UI from the Unity scene without entering Play mode.
    /// This uses Unity's own canvas, TextMeshPro meshes, sprites, materials, and scene transforms.
    /// </summary>
    public static class InHeadsetUiRenderer
    {
        private const string ScenePath = "Assets/Scenes/QuestX.unity";
        private const string ChapBirdAssetPath =
            "Assets/Art/Vectors/Branding/chap-bird-4000.png";
        private const string UiObjectName = "QuestXUI";
        private const string EyeObjectName = "CenterEyeAnchor";
        private const string OutputArgument = "-questXUiOutput";
        private const int RenderLayer = 30;
        private const int Width = 1920;
        private const int Height = 1080;

        [MenuItem("QuestX/Render In-Headset UI")]
        public static void RenderFromMenu()
        {
            Render();
            AssetDatabase.Refresh();
        }

        [MenuItem("QuestX/Apply Chap Bird Logo")]
        public static void ApplyChapBirdLogo()
        {
            Sprite chapBird = ConfigureAndLoadChapBirdSprite();

            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            GameObject ui = GameObject.Find(UiObjectName);
            Transform logoPanel = ui != null ? ui.transform.Find("LogoPanel") : null;
            Transform logo = logoPanel != null ? logoPanel.Find("Logo") : null;
            if (logoPanel == null || logo == null)
            {
                throw new InvalidOperationException(
                    $"Could not find '{UiObjectName}/LogoPanel/Logo' in '{ScenePath}'."
                );
            }

            Component logoGraphic = logo
                .GetComponents<Component>()
                .FirstOrDefault(component =>
                    component != null
                    && new SerializedObject(component).FindProperty("m_Sprite") != null
                );
            if (logoGraphic == null)
            {
                throw new InvalidOperationException(
                    "The logo object does not contain a sprite-backed UI component."
                );
            }

            var serializedGraphic = new SerializedObject(logoGraphic);
            SerializedProperty spriteProperty = serializedGraphic.FindProperty("m_Sprite");
            spriteProperty.objectReferenceValue = chapBird;
            SerializedProperty preserveAspect = serializedGraphic.FindProperty("m_PreserveAspect");
            if (preserveAspect != null)
            {
                preserveAspect.boolValue = true;
            }
            serializedGraphic.ApplyModifiedPropertiesWithoutUndo();

            logoPanel.gameObject.SetActive(true);
            EditorUtility.SetDirty(logoPanel.gameObject);
            EditorUtility.SetDirty(logoGraphic);
            EditorSceneManager.MarkSceneDirty(scene);
            if (!EditorSceneManager.SaveScene(scene))
            {
                throw new IOException($"Unity could not save '{ScenePath}'.");
            }

            Debug.Log($"QUESTX_CHAP_BIRD_APPLIED: {ChapBirdAssetPath}");
        }

        [MenuItem("QuestX/Configure Chap Bird PNG")]
        public static void ConfigureChapBirdPng()
        {
            Sprite chapBird = ConfigureAndLoadChapBirdSprite();
            if (
                !AssetDatabase.TryGetGUIDAndLocalFileIdentifier(
                    chapBird,
                    out string guid,
                    out long localFileId
                )
            )
            {
                throw new InvalidOperationException(
                    $"Unity could not resolve the asset identifiers for '{ChapBirdAssetPath}'."
                );
            }

            Debug.Log(
                $"QUESTX_CHAP_BIRD_PNG_READY: guid={guid} fileID={localFileId} path={ChapBirdAssetPath}"
            );
        }

        /// <summary>
        /// Batch-mode entry point. Optionally pass
        /// -questXUiOutput &lt;absolute-or-project-relative-path&gt;.
        /// </summary>
        public static void Render()
        {
            string outputPath = ResolveOutputPath();
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

            EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);

            GameObject ui = GameObject.Find(UiObjectName);
            if (ui == null)
            {
                throw new InvalidOperationException(
                    $"Could not find '{UiObjectName}' in scene '{ScenePath}'."
                );
            }

            GameObject eye = GameObject.Find(EyeObjectName);
            if (eye == null)
            {
                throw new InvalidOperationException(
                    $"Could not find '{EyeObjectName}' in scene '{ScenePath}'."
                );
            }

            // Isolate the UI from the controller models and other Quest scene objects.
            SetLayerRecursively(ui, RenderLayer);

            GameObject cameraObject = new GameObject("QuestX UI Capture Camera")
            {
                hideFlags = HideFlags.HideAndDontSave,
            };
            UnityCamera captureCamera = cameraObject.AddComponent<UnityCamera>();
            RenderTexture renderTexture = null;
            Texture2D image = null;
            RenderTexture previousActive = RenderTexture.active;

            try
            {
                UnityCamera sceneEyeCamera = eye.GetComponent<UnityCamera>();
                Bounds visibleUiBounds = CalculateVisibleUiBounds(ui);
                float sceneDistance = Mathf.Abs(
                    Vector3.Dot(visibleUiBounds.center - eye.transform.position, ui.transform.forward)
                );
                float viewingDistance = Mathf.Max(sceneDistance, 0.1f);
                captureCamera.transform.SetPositionAndRotation(
                    visibleUiBounds.center - ui.transform.forward * viewingDistance,
                    ui.transform.rotation
                );
                captureCamera.clearFlags = CameraClearFlags.SolidColor;
                captureCamera.backgroundColor = new Color32(112, 118, 124, 255);
                captureCamera.orthographic = false;
                captureCamera.fieldOfView = sceneEyeCamera != null ? sceneEyeCamera.fieldOfView : 90f;
                captureCamera.nearClipPlane = 0.05f;
                captureCamera.farClipPlane = 10f;
                captureCamera.aspect = (float)Width / Height;
                captureCamera.cullingMask = 1 << RenderLayer;
                captureCamera.allowHDR = false;
                captureCamera.allowMSAA = true;

                Canvas canvas = ui.GetComponent<Canvas>();
                if (canvas == null || canvas.renderMode != RenderMode.WorldSpace)
                {
                    throw new InvalidOperationException(
                        $"'{UiObjectName}' must have a world-space Canvas component."
                    );
                }

                canvas.worldCamera = captureCamera;
                foreach (TMP_Text text in ui.GetComponentsInChildren<TMP_Text>(true))
                {
                    text.ForceMeshUpdate(ignoreActiveState: true, forceTextReparsing: true);
                }

                Canvas.ForceUpdateCanvases();

                renderTexture = new RenderTexture(
                    Width,
                    Height,
                    24,
                    RenderTextureFormat.ARGB32,
                    RenderTextureReadWrite.sRGB
                )
                {
                    antiAliasing = 4,
                    name = "QuestX UI Capture",
                    hideFlags = HideFlags.HideAndDontSave,
                };
                renderTexture.Create();

                captureCamera.targetTexture = renderTexture;
                captureCamera.Render();

                RenderTexture.active = renderTexture;
                image = new Texture2D(Width, Height, TextureFormat.RGBA32, false, false)
                {
                    hideFlags = HideFlags.HideAndDontSave,
                };
                image.ReadPixels(new Rect(0, 0, Width, Height), 0, 0, false);
                image.Apply(false, false);

                File.WriteAllBytes(outputPath, image.EncodeToPNG());
                Debug.Log($"QUESTX_UI_RENDER_SUCCESS: {outputPath}");
            }
            finally
            {
                RenderTexture.active = previousActive;
                captureCamera.targetTexture = null;

                if (image != null)
                {
                    UnityEngine.Object.DestroyImmediate(image);
                }

                if (renderTexture != null)
                {
                    renderTexture.Release();
                    UnityEngine.Object.DestroyImmediate(renderTexture);
                }

                UnityEngine.Object.DestroyImmediate(cameraObject);
            }
        }

        private static string ResolveOutputPath()
        {
            string[] args = Environment.GetCommandLineArgs();
            int argumentIndex = Array.IndexOf(args, OutputArgument);
            string requested = argumentIndex >= 0 && argumentIndex + 1 < args.Length
                ? args[argumentIndex + 1]
                : Path.Combine("..", "questx-in-headset-ui-preview.png");

            return Path.GetFullPath(requested, Directory.GetCurrentDirectory());
        }

        private static Sprite ConfigureAndLoadChapBirdSprite()
        {
            AssetDatabase.ImportAsset(
                ChapBirdAssetPath,
                ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate
            );

            TextureImporter importer = AssetImporter.GetAtPath(ChapBirdAssetPath) as TextureImporter;
            if (importer == null)
            {
                throw new InvalidOperationException(
                    $"Unity did not create a texture importer for '{ChapBirdAssetPath}'."
                );
            }

            importer.textureType = TextureImporterType.Sprite;
            importer.spriteImportMode = SpriteImportMode.Single;
            importer.spritePixelsPerUnit = 100f;
            importer.alphaIsTransparency = true;
            importer.mipmapEnabled = true;
            importer.filterMode = FilterMode.Trilinear;
            importer.wrapMode = TextureWrapMode.Clamp;
            importer.npotScale = TextureImporterNPOTScale.None;
            importer.textureCompression = TextureImporterCompression.Uncompressed;
            importer.maxTextureSize = 2048;
            importer.isReadable = false;
            importer.SaveAndReimport();

            Sprite chapBird = AssetDatabase
                .LoadAllAssetsAtPath(ChapBirdAssetPath)
                .OfType<Sprite>()
                .FirstOrDefault();
            if (chapBird == null)
            {
                throw new InvalidOperationException(
                    $"Unity did not import a Sprite from '{ChapBirdAssetPath}'."
                );
            }

            return chapBird;
        }

        private static void SetLayerRecursively(GameObject root, int layer)
        {
            root.layer = layer;
            foreach (Transform child in root.transform.Cast<Transform>())
            {
                SetLayerRecursively(child.gameObject, layer);
            }
        }

        private static Bounds CalculateVisibleUiBounds(GameObject ui)
        {
            Graphic[] graphics = ui.GetComponentsInChildren<Graphic>(false);
            if (graphics.Length == 0)
            {
                throw new InvalidOperationException(
                    $"'{UiObjectName}' does not contain any active Unity UI graphics."
                );
            }

            var corners = new Vector3[4];
            bool initialized = false;
            Bounds bounds = default;

            foreach (Graphic graphic in graphics)
            {
                graphic.rectTransform.GetWorldCorners(corners);
                foreach (Vector3 corner in corners)
                {
                    if (!initialized)
                    {
                        bounds = new Bounds(corner, Vector3.zero);
                        initialized = true;
                    }
                    else
                    {
                        bounds.Encapsulate(corner);
                    }
                }
            }

            return bounds;
        }
    }
}
