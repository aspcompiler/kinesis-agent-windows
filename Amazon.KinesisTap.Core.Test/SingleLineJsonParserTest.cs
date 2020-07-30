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
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace Amazon.KinesisTap.Core.Test
{
    public class SingleLineJsonParserTest
    {
        const string LOG = @"{""ul-log-data"":{""method_name"":""UserProfile"",""module_name"":""ACMEController""},""ul-tag-status"":""INFO"",""ul-timestamp-epoch"":1537519130972, ""Timestamp"":""2018-09-21T08:38:50.972Z""}
{""ul-log-data"":{""http_url"":""http://acme/org"",""http_request_headers"":{""Accept"":""*/*"",""Accept-Encoding"":""gzip;deflate""},""http_response_code"":401},""ul-tag-status"":""INFO"",""ul-timestamp-epoch"":1537519131241, ""Timestamp"":""2018-09-21T08:38:51.241Z""}
";
        [Theory]
        [InlineData("JsonLog1")]
        [InlineData("JsonLog2")]
        public void TestJsonLog(string sourceId)
        {
            using (Stream stream = Utility.StringToStream(LOG))
            using (StreamReader sr = new StreamReader(stream))
            {
                var config = TestUtility.GetConfig("Sources", sourceId);
                var records = ParseRecords(sr, config);

                Assert.Equal(2, records.Count);

                var record0 = records[0];
                Assert.Equal(new DateTime(2018, 9, 21, 8, 38, 50, 972), records[0].Timestamp);
                Assert.Equal("UserProfile", record0.Data["ul-log-data"]["method_name"]);
                Assert.Equal("INFO", record0.Data["ul-tag-status"]);
            }
        }

        /// <summary>
        /// Test parsing a log file where lines may not be written completely at first
        /// </summary>
        [Fact]
        public void TestParseInterleavedWrites()
        {
            var parser = new SingleLineJsonParser(null, null, NullLogger.Instance);
            var testFile = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
            var context = new LogContext();

            try
            {
                var records = ParseFile(parser, testFile, context);
                Assert.Empty(records);

                File.AppendAllText(testFile, $"{{\"a\":1}}{Environment.NewLine}");
                records = ParseFile(parser, testFile, context);
                Assert.Single(records);

                File.AppendAllText(testFile, $"{{\"b\":");
                records = ParseFile(parser, testFile, context);
                Assert.Empty(records);

                File.AppendAllText(testFile, $"2}}{Environment.NewLine}");
                records = ParseFile(parser, testFile, context);
                Assert.Single(records);
            }
            finally
            {
                File.Delete(testFile);
            }
        }

        /// <summary>
        /// The parser should ignore invalid JSON.
        /// </summary>
        [Fact]
        public void TestParseInvalidLines()
        {
            var parser = new SingleLineJsonParser(null, null, NullLogger.Instance);
            var testFile = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
            var context = new LogContext();

            try
            {
                File.AppendAllText(testFile, $"{{\"a\":1}}{Environment.NewLine}");
                var records = ParseFile(parser, testFile, context);
                Assert.Single(records);

                // write an in valid json line
                File.AppendAllText(testFile, $"{{\"b\":");
                records = ParseFile(parser, testFile, context);
                Assert.Empty(records);

                File.AppendAllText(testFile, Environment.NewLine);
                records = ParseFile(parser, testFile, context);
                Assert.Empty(records);

                File.AppendAllText(testFile, $"{{\"c\":3}}{Environment.NewLine}");
                records = ParseFile(parser, testFile, context);
                Assert.Single(records);
            }
            finally
            {
                File.Delete(testFile);
            }
        }

        /// <summary>
        /// Make sure the parser can handle long records that are not written to the file immediately.
        /// </summary>
        [Theory]
        [InlineData(FileLineReader.MinimumBufferSize)]
        [InlineData(FileLineReader.MinimumBufferSize * 2)]
        public void TestParseLongRecords(int size)
        {
            var parser = new SingleLineJsonParser(null, null, NullLogger.Instance);
            var testFile = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
            var context = new LogContext();

            var value = new string('a', size);

            try
            {
                File.AppendAllText(testFile, $"{{\"a\":\"{value}\"}}{Environment.NewLine}");
                var records = ParseFile(parser, testFile, context);
                Assert.Single(records);
                Assert.Equal(value, (string)records[0].Data["a"]);

                File.AppendAllText(testFile, $"{{\"b\":");
                records = ParseFile(parser, testFile, context);
                Assert.Empty(records);

                File.AppendAllText(testFile, $"\"{value}\"}}{Environment.NewLine}");
                records = ParseFile(parser, testFile, context);
                Assert.Single(records);
                Assert.Equal(value, (string)records[0].Data["b"]);
            }
            finally
            {
                File.Delete(testFile);
            }
        }

        /// <summary>
        /// Make sure that when a file is truncated, the parser is able to detect that.
        /// </summary>
        [Fact]
        public void TestParseTruncatedFile()
        {
            var parser = new SingleLineJsonParser(null, null, NullLogger.Instance);
            var testFile = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
            var context = new LogContext();

            var value1 = new string('1', 1024);
            var value2 = new string('2', 1024);
            try
            {
                File.AppendAllText(testFile, $"{{\"a\":\"{value1}\"}}");
                var records = ParseFile(parser, testFile, context);
                Assert.Empty(records);

                File.Delete(testFile);
                context.Position = 0;
                File.AppendAllText(testFile, $"{{\"a\":\"{value2}\"}}{Environment.NewLine}");

                records = ParseFile(parser, testFile, context);
                Assert.Single(records);
                Assert.Equal(value2, (string)records[0].Data["a"]);
            }
            finally
            {
                File.Delete(testFile);
            }
        }

        private static List<IEnvelope<JObject>> ParseFile(SingleLineJsonParser parser, string file, LogContext context)
        {
            using (var readStream = new FileStream(file, FileMode.OpenOrCreate, FileAccess.Read, FileShare.ReadWrite))
            using (var sr = new StreamReader(readStream))
            {
                var records = parser.ParseRecords(sr, context).ToList();
                context.Position = readStream.Position;
                return records;
            }
        }

        private static List<IEnvelope<JObject>> ParseRecords(StreamReader sr, Microsoft.Extensions.Configuration.IConfigurationSection config)
        {
            string timetampFormat = config["TimestampFormat"];
            string timestampField = config["TimestampField"];
            var parser = new SingleLineJsonParser(timestampField, timetampFormat, NullLogger.Instance);
            var records = parser.ParseRecords(sr, new DelimitedLogContext()).ToList();
            return records;
        }
    }
}
