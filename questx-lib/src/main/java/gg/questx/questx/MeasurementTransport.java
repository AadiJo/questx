/*
* QUESTX
  https://github.com/QuestX
* Copyright (C) 2026 QuestX
*
* This program is free software: you can redistribute it and/or modify
* it under the terms of the MIT License as published.
*/
package gg.questx.questx;

/** Selects one measurement transport for the lifetime of a QuestX instance. */
public enum MeasurementTransport {
  /** Atomic numeric arrays. Requires a Quest app with v2 measurement support. */
  V2,
  /** Compatibility with Quest apps publishing only vioData/aprilTagData protobufs. */
  LEGACY_PROTOBUF
}
