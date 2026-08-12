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
import gg.questx.questx.protos.generated.Data;
import us.hebi.quickbuf.Descriptors;

/** WPILib Protobuf layer for Quest AprilTag measurements. */
public class AprilTagDataProto
    implements Protobuf<Data.ProtobufQuestXAprilTagData, Data.ProtobufQuestXAprilTagData> {
  @Override
  public Class<Data.ProtobufQuestXAprilTagData> getTypeClass() {
    return Data.ProtobufQuestXAprilTagData.class;
  }

  @Override
  public Descriptors.Descriptor getDescriptor() {
    return Data.ProtobufQuestXAprilTagData.getDescriptor();
  }

  @Override
  public Data.ProtobufQuestXAprilTagData createMessage() {
    return Data.ProtobufQuestXAprilTagData.newInstance();
  }

  @Override
  public Data.ProtobufQuestXAprilTagData unpack(Data.ProtobufQuestXAprilTagData msg) {
    return msg.clone();
  }

  @Override
  public void pack(Data.ProtobufQuestXAprilTagData msg, Data.ProtobufQuestXAprilTagData value) {
    msg.copyFrom(value);
  }
}
