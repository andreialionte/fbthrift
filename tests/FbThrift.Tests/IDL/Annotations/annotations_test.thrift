/*
 * Copyright (c) Meta Platforms, Inc. and affiliates.
 * Licensed under the Apache License, Version 2.0.
 */

package "test.dev.csharp.tests.annotations"

namespace csharp FbThrift.Tests.Generated.Annotations

@thrift.Uri("fbthrift://test.com/structs/AnnotatedStruct")
struct AnnotatedStruct {
  1: required i64 id;
  @thrift.Adapter("CustomStringAdapter")
  2: optional string name;
  @thrift.Deprecated("Use newField instead")
  3: optional string oldField;
  4: optional string newField;
}

@thrift.Uri("fbthrift://test.com/enums/AnnotatedEnum")
enum AnnotatedEnum {
  @thrift.Deprecated("Use VALUE_TWO instead")
  VALUE_ONE = 0,
  VALUE_TWO = 1,
}

@thrift.Uri("fbthrift://test.com/unions/AnnotatedUnion")
union AnnotatedUnion {
  @thrift.Adapter("CustomIntAdapter")
  1: i32 number;
  2: string text;
}

@thrift.Uri("fbthrift://test.com/exceptions/AnnotatedException")
exception AnnotatedException {
  @thrift.Adapter("CustomErrorAdapter")
  1: i32 code;
  2: string message;
}

@thrift.Uri("fbthrift://test.com/services/AnnotatedService")
service AnnotatedService {
  @thrift.Idempotent
  i64 getId(1: required string key);
  
  @thrift.Oneway
  void logEvent(1: required string event);
  
  @thrift.Stream
  stream<string> getEvents(1: required string filter);
  
  @thrift.Sink
  i32 processEvents(sink<string> events);
  
  @thrift.Interaction
  interaction i32 longRunningTask();
}