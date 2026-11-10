/*
 * Copyright (c) Meta Platforms, Inc. and affiliates.
 * Licensed under the Apache License, Version 2.0.
 */

package "test.dev.csharp.tests.services"

namespace csharp FbThrift.Tests.Generated.Services

struct User {
  1: required i64 id;
  2: required string name;
  3: optional string email;
}

struct UserRequest {
  1: required i64 id;
}

struct CreateUserRequest {
  1: required User user;
}

struct UserListResponse {
  1: required list<User> users;
}

exception UserNotFound {
  1: required string message;
  2: required i64 userId;
}

exception InvalidUserData {
  1: required string field;
  2: required string reason;
}

service UserService {
  User getUser(1: required UserRequest request) throws (1: UserNotFound notFound);
  list<User> getUsers(1: optional list<i64> ids);
  User createUser(1: required CreateUserRequest request) throws (1: InvalidUserData invalidData);
  bool deleteUser(1: required UserRequest request);
  
  // Streaming methods
  stream<User> getUserStream(1: required UserRequest request);
  sink<i64, i64> batchDelete();
  bidi<string, string> chat();
}