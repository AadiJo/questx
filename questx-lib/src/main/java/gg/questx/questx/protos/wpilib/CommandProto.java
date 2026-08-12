/*
* QUESTX
  https://github.com/QuestX
* Copyright (C) 2025 QuestX
*
* This program is free software: you can redistribute it and/or modify
* it under the terms of the MIT License as published.
*/
package gg.questx.questx.protos.wpilib;

import edu.wpi.first.util.protobuf.Protobuf;
import gg.questx.questx.protos.generated.Commands;
import us.hebi.quickbuf.Descriptors;

/**
 * WPILib Protobuf serialization adapter for QuestX command messages.
 *
 * <p>This class provides the necessary integration between QuestX's protocol buffer command
 * definitions and WPILib's NetworkTables protobuf system. It handles the serialization and
 * deserialization of command messages sent from robot code to the Quest headset.
 *
 * <p>This adapter is used internally by the {@link gg.questx.questx.QuestX} class and typically
 * does not need to be used directly by robot code.
 *
 * <h2>Supported Commands</h2>
 *
 * <p>This adapter handles all QuestX command types including:
 *
 * <ul>
 *   <li><strong>Pose Reset:</strong> Commands to reset the Quest's tracking to a known robot pose
 *   <li><strong>Future Commands:</strong> Extensible for additional command types as needed
 * </ul>
 *
 * <h2>Protocol Buffer Integration</h2>
 *
 * <p>The class implements WPILib's {@link edu.wpi.first.util.protobuf.Protobuf} interface to
 * provide seamless integration with NetworkTables. This enables:
 *
 * <ul>
 *   <li>Efficient binary serialization over the network
 *   <li>Type-safe message handling
 *   <li>Automatic message validation and error handling
 *   <li>Cross-platform compatibility (Java robot code ↔ C# Quest app)
 * </ul>
 *
 * @see gg.questx.questx.QuestX#setPose(edu.wpi.first.math.geometry.Pose3d)
 * @see gg.questx.questx.protos.generated.Commands.ProtobufQuestXCommand
 * @see edu.wpi.first.util.protobuf.Protobuf
 * @since 2025.1.0
 */
public class CommandProto
    implements Protobuf<Commands.ProtobufQuestXCommand, Commands.ProtobufQuestXCommand> {
  @Override
  public Class<Commands.ProtobufQuestXCommand> getTypeClass() {
    return Commands.ProtobufQuestXCommand.class;
  }

  @Override
  public Descriptors.Descriptor getDescriptor() {
    return Commands.ProtobufQuestXCommand.getDescriptor();
  }

  @Override
  public Commands.ProtobufQuestXCommand createMessage() {
    return Commands.ProtobufQuestXCommand.newInstance();
  }

  @Override
  public Commands.ProtobufQuestXCommand unpack(Commands.ProtobufQuestXCommand msg) {
    return msg.clone();
  }

  @Override
  public void pack(Commands.ProtobufQuestXCommand msg, Commands.ProtobufQuestXCommand value) {
    msg.copyFrom(value);
  }

  @Override
  public boolean isImmutable() {
    return true;
  }
}
