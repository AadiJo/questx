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

/** WPILib Protobuf layer for Quest VIO data. */
public class VioDataProto
    implements Protobuf<Data.ProtobufQuestXVioData, Data.ProtobufQuestXVioData> {
  @Override
  public Class<Data.ProtobufQuestXVioData> getTypeClass() {
    return Data.ProtobufQuestXVioData.class;
  }

  @Override
  public Descriptors.Descriptor getDescriptor() {
    return Data.ProtobufQuestXVioData.getDescriptor();
  }

  @Override
  public Data.ProtobufQuestXVioData createMessage() {
    return Data.ProtobufQuestXVioData.newInstance();
  }

  @Override
  public Data.ProtobufQuestXVioData unpack(Data.ProtobufQuestXVioData msg) {
    return msg.clone();
  }

  @Override
  public void pack(Data.ProtobufQuestXVioData msg, Data.ProtobufQuestXVioData value) {
    msg.copyFrom(value);
  }
}
