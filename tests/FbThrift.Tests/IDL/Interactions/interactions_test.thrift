/*
 * Copyright (c) Meta Platforms, Inc. and affiliates.
 * Licensed under the Apache License, Version 2.0.
 */

package "test.dev.csharp.tests.interactions"

namespace csharp FbThrift.Tests.Generated.Interactions

struct InteractionRequest {
  1: required i64 id;
  2: optional string data;
}

struct InteractionResponse {
  1: required i64 id;
  2: required string result;
}

struct StreamItem {
  1: required i32 sequence;
  2: required string data;
}

struct SinkRequest {
  1: required i32 sequence;
  2: required string chunk;
}

struct SinkResponse {
  1: required i64 totalProcessed;
}

struct BidiRequest {
  1: required i32 sequence;
  2: required string message;
}

struct BidiResponse {
  1: required i32 sequence;
  2: required string reply;
}

@thrift.Interaction
struct MyInteraction {
  1: required string name;
}

service InteractionService {
  InteractionResponse callMethod(1: required InteractionRequest request);
  oneway void notifyEvent(1: required string event);
  stream<StreamItem> getStream(1: required InteractionRequest request);
  sink<SinkRequest, SinkResponse> sinkData();
  bidi<BidiRequest, BidiResponse> bidirectionalChat();
  @thrift.Interaction
  MyInteraction createInteraction(1: required string name);
  @thrift.Interaction
  void terminateInteraction(1: required string name);
}