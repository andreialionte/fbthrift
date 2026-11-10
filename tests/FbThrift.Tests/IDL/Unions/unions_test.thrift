/*
 * Copyright (c) Meta Platforms, Inc. and affiliates.
 * Licensed under the Apache License, Version 2.0.
 */

package "test.dev.csharp.tests.unions"

namespace csharp FbThrift.Tests.Generated.Unions

enum MyEnum {
  MyValue1 = 0,
  MyValue2 = 1,
}

struct MyDataItem {}

union MyUnion {
  1: MyEnum myEnum;
  2: MyDataItem myDataItem;
}

union UnionToBeRenamed {
  1: i32 reserved_field;
}

typedef list<i32> IntList
typedef map<string, i32> StringIntMap

union TypedefContainerUnion {
  1: IntList int_list_field;
  2: StringIntMap string_int_map_field;
}

union BinaryUnion {
  1: binary data;
}

union NestedUnion {
  1: MyUnion union_field;
  2: string simple_field;
}