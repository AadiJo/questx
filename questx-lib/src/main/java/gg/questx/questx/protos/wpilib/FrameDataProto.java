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

/** WPILib Protobuf layer for FrameData Protobuf */
public class FrameDataProto
    implements Protobuf<Data.ProtobufQuestXFrameData, Data.ProtobufQuestXFrameData> {
  @Override
  public Class<Data.ProtobufQuestXFrameData> getTypeClass() {
    return Data.ProtobufQuestXFrameData.class;
  }

  @Override
  public Descriptors.Descriptor getDescriptor() {
    return Data.ProtobufQuestXFrameData.getDescriptor();
  }

  @Override
  public Data.ProtobufQuestXFrameData createMessage() {
    return Data.ProtobufQuestXFrameData.newInstance();
  }

  @Override
  public Data.ProtobufQuestXFrameData unpack(Data.ProtobufQuestXFrameData msg) {
    return msg.clone();
  }

  @Override
  public void pack(Data.ProtobufQuestXFrameData msg, Data.ProtobufQuestXFrameData value) {
    msg.copyFrom(value);
  }
}
