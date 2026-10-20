// API client for the 3D views

import { QuestXApi } from './questx'
import type { SceneLayout, SceneResponse } from '../types'

class SceneApi extends QuestXApi {
  // Polling this also keeps the headset building per-tag poses; it stops a few
  // seconds after the last request.
  async getScene(): Promise<SceneResponse> {
    return this.request<SceneResponse>('/api/scene')
  }

  async getLayout(): Promise<SceneLayout> {
    return this.request<SceneLayout>('/api/scene/layout')
  }
}

export const sceneApi = new SceneApi()
