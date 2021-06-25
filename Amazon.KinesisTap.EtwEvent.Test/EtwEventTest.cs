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
using Amazon.KinesisTap.Core;
using Xunit;
using Microsoft.Diagnostics.Tracing;
using Amazon.KinesisTap.Windows;
using Amazon.KinesisTap.Test.Common;
using Microsoft.Extensions.Logging.Abstractions;

namespace Amazon.KinesisTap.EtwEvent.Test
{
    /// <summary>
    /// Unit test for the ETW event source 
    /// </summary>
    public class EtwEventTest
    {
        /// <summary>
        /// Create a mock ETW event source, start it (which injects a mock event), stop it, and confirm that a mock event was recorded and has values we 
        /// expect.
        /// </summary>
        [WindowsOnlyFact]
        public void TestEventProcessing()
        {
            //Configure
            var mockSink = new ListEventSink();

            using (EtwEventSource mockEtwSource = new MockEtwEventSource(MockTraceEvent.ClrProviderName, TraceEventLevel.Verbose, ulong.MaxValue,
                new PluginContext(null, NullLogger.Instance, null)))
            {
                mockEtwSource.Subscribe(mockSink);

                //Execute
                mockEtwSource.Start();
                mockEtwSource.Stop();
            }

            //Verify
            Assert.True(mockSink.Count == 1);
            Assert.True(mockSink[0] is EtwEventEnvelope);
            Assert.True(MockEtwEventEnvelope.ValidateEnvelope((EtwEventEnvelope)mockSink[0]), "Event envelope data or event data does not match expected values.");
        }
    }
}
