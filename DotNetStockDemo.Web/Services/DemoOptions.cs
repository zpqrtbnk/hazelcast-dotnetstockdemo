// Copyright (c) 2008-2022, Hazelcast, Inc. All Rights Reserved.
// 
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
// 
// http://www.apache.org/licenses/LICENSE-2.0
// 
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

namespace DotNetStockDemo.Web.Services;

public class DemoOptions
{
    public string? KafkaServer { get; set; }

    public int KafkaPort { get; set; }

    public string? KafkaTopicName { get; set; }

    public string? HazelcastServer { get; set; }

    public int HazelcastPort { get; set; }

    public string? HazelcastClusterName { get; set; }
}