/*
 * Copyright 2018 Amazon.com, Inc. or its affiliates. All Rights Reserved.
 * 
 * Licensed under the Apache License, Version 2.0 (the "License").
 * You may not use this file except in compliance with the License.
 * A copy of the License is located at
 * 
 *  http://aws.amazon.com/apache2.0
 * 
 * or in the "license" file accompanying this file. This file is distributed
 * on an "AS IS" BASIS, WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either
 * express or implied. See the License for the specific language governing
 * permissions and limitations under the License.
 */
namespace Amazon.KinesisTap.Core
{
    //A pipe is both source and sink so it can subscribe to a source and be subscribed by a sink
    public interface IPipe : IEventSource, IEventSink
    {
    }

    public interface IPipe<in TIn, out TOut> : IPipe, IEventSink<TIn>, IEventSource<TOut>
    {
    }
}
