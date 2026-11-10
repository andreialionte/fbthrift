/*
 * Copyright (c) Meta Platforms, Inc. and affiliates.
 * Licensed under the Apache License, Version 2.0.
 */

package "test.dev.csharp.tests.adapters"

namespace csharp FbThrift.Tests.Generated.Adapters

// Custom type adapter for string to int conversion
struct CustomStringToIntAdapter {
  // This would be implemented in C# code
}

// Custom type adapter for DateTime
struct DateTimeAdapter {
  // This would be implemented in C# code
}

struct AdapterStruct {
  1: required i64 id;
  @thrift.Adapter("CustomStringToIntAdapter")
  2: optional string nameAsInt;
  @thrift.Adapter("DateTimeAdapter")
  3: optional i64 timestamp;
}

// Field adapter with context
struct AdapterContextStruct {
  1: required i64 id;
  @thrift.Adapter("CustomFieldAdapter")
  2: optional string contextualField;
}

// Type adapter with template
@thrift.Template("std::vector<{{type}}>")
typedef list<i32> IntVector

struct TypedefAdapterStruct {
  1: IntVector numbers;
}