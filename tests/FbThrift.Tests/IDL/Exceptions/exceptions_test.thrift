/*
 * Copyright (c) Meta Platforms, Inc. and affiliates.
 * Licensed under the Apache License, Version 2.0.
 */

package "test.dev.csharp.tests.exceptions"

namespace csharp FbThrift.Tests.Generated.Exceptions

struct MyExceptionData {
  1: i64 MyIntField;
  2: string MyStringField;
}

exception MyException {
  1: i64 MyIntField;
  2: string MyStringField;
}

exception MyExceptionWithMessage {
  1: i64 MyIntField;
  @thrift.ExceptionMessage
  2: string MyStringField;
}

exception MyExceptionWithOptionalMessage {
  @thrift.ExceptionMessage
  1: optional string errorMessage;
}

exception NestedException {
  1: required MyExceptionData data;
  2: optional string context;
}

exception ExceptionWithContainers {
  1: list<string> messages;
  2: map<string, i64> codes;
}