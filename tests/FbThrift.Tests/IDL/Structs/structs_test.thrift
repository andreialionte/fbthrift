/*
 * Copyright (c) Meta Platforms, Inc. and affiliates.
 * Licensed under the Apache License, Version 2.0.
 */

package "test.dev.csharp.tests.structs"

namespace csharp FbThrift.Tests.Generated.Structs

enum MyEnum {
  MyValue1 = 0,
  MyValue2 = 1,
}

struct MyDataItem {}

struct MyStruct {
  1: required i64 MyIntField;
  2: required string MyStringField;
  3: optional MyDataItem MyDataField;
  4: optional MyEnum myEnum;
  5: optional bool oneway;
  6: optional bool readonly;
  7: optional bool idempotent;
}

struct Containers {
  1: list<i32> I32List;
  2: set<string> StringSet;
  3: map<string, i64> StringToI64Map;
}

typedef list<i32> IntList
typedef set<string> StringSet
typedef map<string, i64> StringToI64Map

struct TypedefContainerStruct {
  1: IntList int_list_field;
  2: StringSet string_set_field;
  3: StringToI64Map string_map_field;
}

struct OptionalFieldsStruct {
  1: optional string optional_string;
  2: optional MyDataItem optional_struct;
  3: optional list<i32> optional_list;
}

struct NestedStruct {
  1: required MyStruct inner;
  3: list<MyStruct> items;
  4: map<string, MyStruct> map_items;
}