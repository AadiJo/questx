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

/** WPILib Protobuf layer for CommandResponse Protobuf */
public class CommandResponseProto
    implements Protobuf<
        Commands.ProtobufQuestXCommandResponse, Commands.ProtobufQuestXCommandResponse> {
  @Override
  public Class<Commands.ProtobufQuestXCommandResponse> getTypeClass() {
    return Commands.ProtobufQuestXCommandResponse.class;
  }

  @Override
  public Descriptors.Descriptor getDescriptor() {
    return Commands.ProtobufQuestXCommandResponse.getDescriptor();
  }

  @Override
  public Commands.ProtobufQuestXCommandResponse createMessage() {
    return Commands.ProtobufQuestXCommandResponse.newInstance();
  }

  @Override
  public Commands.ProtobufQuestXCommandResponse unpack(Commands.ProtobufQuestXCommandResponse msg) {
    return msg.clone();
  }

  @Override
  public void pack(
      Commands.ProtobufQuestXCommandResponse msg, Commands.ProtobufQuestXCommandResponse value) {
    msg.copyFrom(value);
  }

  @Override
  public boolean isImmutable() {
    return true;
  }
}
