/*
 * Copyright (c) Meta Platforms, Inc. and affiliates.
 * Licensed under the Apache License, Version 2.0.
 */

package "test.dev.csharp.tests.constants"

namespace csharp FbThrift.Tests.Generated.Constants

const bool FLAG = true;
const byte OFFSET = -10;
const i16 COUNT = 200;
const i32 MASK = 0xFA12EE;
const i64 LARGE_NUMBER = 9223372036854775807;
const double E = 2.718281828459045;
const string DATE = "June 28, 2017";
const binary BINARY_DATA = "\x00\x01\x02\x03\xFF\xFE\xFD";
const list<i32> LIST_CONSTANT = [1, 2, 3, 4, 5];
const map<string, i32> MAP_CONSTANT = {"one": 1, "two": 2, "three": 3};