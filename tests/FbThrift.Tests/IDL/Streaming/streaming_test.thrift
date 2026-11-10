/*
 * Copyright (c) Meta Platforms, Inc. and affiliates.
 * Licensed under the Apache License, Version 2.0.
 */

package "test.dev.csharp.tests.streaming"

namespace csharp FbThrift.Tests.Generated.Streaming

struct StreamChunk {
  1: required i32 sequence;
  2: required string data;
  3: optional bool isLast;
}

struct StreamRequest {
  1: required string filter;
  2: optional i32 limit;
}

struct SinkChunk {
  1: required i32 sequence;
  2: required binary data;
}

struct SinkResult {
  1: required i64 totalBytes;
  3: required i32 chunkCount;
}

struct BidiMessage {
  1: required i32 sequence;
  2: required string payload;
}

service StreamingService {
  stream<StreamChunk> streamData(1: required StreamRequest request);
  sink<SinkChunk, SinkResult> uploadData();
  bidi<string, string> bidirectionalStream();
  ResponseAndStream<StreamRequest, StreamChunk> getDataWithStream(1: required StreamRequest request);
  SinkClient<SinkChunk, SinkResult> uploadWithResult();
  BiDiStream<BidiMessage, BidiMessage> chat(1: required string roomId);
}