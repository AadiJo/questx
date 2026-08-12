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

/** WPILib Protobuf layer for DeviceData Protobuf */
public class DeviceDataProto
    implements Protobuf<Data.ProtobufQuestXDeviceData, Data.ProtobufQuestXDeviceData> {
  @Override
  public Class<Data.ProtobufQuestXDeviceData> getTypeClass() {
    return Data.ProtobufQuestXDeviceData.class;
  }

  @Override
  public Descriptors.Descriptor getDescriptor() {
    return Data.ProtobufQuestXDeviceData.getDescriptor();
  }

  @Override
  public Data.ProtobufQuestXDeviceData createMessage() {
    return Data.ProtobufQuestXDeviceData.newInstance();
  }

  @Override
  public Data.ProtobufQuestXDeviceData unpack(Data.ProtobufQuestXDeviceData msg) {
    return msg.clone();
  }

  @Override
  public void pack(Data.ProtobufQuestXDeviceData msg, Data.ProtobufQuestXDeviceData value) {
    msg.copyFrom(value);
  }
}
