/*
 * Copyright (c) Meta Platforms, Inc. and affiliates.
 * Licensed under the Apache License, Version 2.0.
 */

package "test.dev.csharp.tests.compression"

namespace csharp FbThrift.Tests.Generated.Compression

struct CompressedData {
  1: required i64 id;
  
  @thrift.Compression(Zstd)
  2: optional binary zstdData;
  
  @thrift.Compression(Zstd, level = 9)
  3: optional binary zstdBestData;
  
  @thrift.Compression(Lz4)
  4: optional binary lz4Data;
  
  @thrift.Compression(Zstd, level = 1)
  5: optional binary zstdFastData;
}

struct CompressedStruct {
  1: required i64 id;
  
  @thrift.Compression(Zstd)
  2: list<string> compressedStrings;
  
  @thrift.Compression(Lz4)
  3: map<string, binary> compressedMap;
  
  @thrift.Compression(Zstd)
  4: set<i64> compressedSet;
}

service CompressionService {
  @thrift.Compression(Zstd)
  CompressedData compressData(1: required CompressedData input);
  
  @thrift.Compression(Lz4)
  CompressedStruct compressStruct(1: required CompressedStruct input);
  
  stream<CompressedData> streamCompressedData(1: required string pattern);
}