﻿using System;
using System.Collections.Generic;
using System.Diagnostics.Eventing.Reader;
using System.Reactive;
using System.Diagnostics;
using System.IO;

using Amazon.KinesisTap.Core;
using Amazon.KinesisTap.Core.Test;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Amazon.KinesisTap.Windows.Test
{
    public class EventLogTest
    {
        [Fact]
        public void TestInitialPositionEOS()
        {
            ListEventSink records = new ListEventSink();

            GenerateAndCaptureEvents(records, null);

            Assert.True(records.Count > 0);
        }


        [Fact]
        public void TestInitialPositionBOS()
        {
            string logName = "Application";
            string logSource = "Test";
            ListEventSink records = new ListEventSink();
            DateTime now = DateTime.Now;
            string sourceId = "TestInitialPositionTimeStamp";
            DeleteExistingBookmarkFile(sourceId);

            using (EventLogSource source = new EventLogSource(logName, null, new PluginContext(null, null, null)))
            {
                source.Subscribe(records);
                source.Id = sourceId;
                source.InitialPosition = InitialPositionEnum.BOS;
                source.Start();

                do
                {
                    EventLog.WriteEntry(logSource, "A fresh message", EventLogEntryType.Information, 0);
                    System.Threading.Thread.Sleep(1000);
                }
                while (source.LastEventLatency > new TimeSpan(0, 0, 1));

                source.Stop();
                Assert.True(records.Count > 0);
                Assert.True(records[0].Timestamp < now);

                //Write some new logs afte the source stop
                DateTime dateTime2 = DateTime.Now;
                string msg = $"Message generated by EvengLogTest {dateTime2}";
                int eventId = (int)(DateTime.Now.Ticks % ushort.MaxValue);
                EventLog.WriteEntry(logSource, msg, EventLogEntryType.Information, eventId);
                System.Threading.Thread.Sleep(1000);

                records.Clear();
                source.Start();
                System.Threading.Thread.Sleep(1000);
                //Should get the record when the souce is stopped
                Assert.True(records.Count > 0);
                Assert.True(records[0].Timestamp >= dateTime2);
            }
        }

        [Fact]
        public void TestInitialPositionTimeStamp()
        {
            string logName = "Application";
            string logSource = "Test";
            ListEventSink records = new ListEventSink();
            DateTime initialTimestamp = DateTime.Now.AddDays(-1);
            DateTime now = DateTime.Now;
            string sourceId = "TestInitialPositionTimeStamp";
            DeleteExistingBookmarkFile(sourceId);

            using (EventLogSource source = new EventLogSource(logName, null, new PluginContext(null, null, null)))
            {
                source.Subscribe(records);
                source.Id = sourceId;
                source.InitialPosition = InitialPositionEnum.Timestamp;
                source.InitialPositionTimestamp = initialTimestamp;
                source.Start();

                do
                {
                    EventLog.WriteEntry(logSource, "A fresh message", EventLogEntryType.Information, 0);
                    System.Threading.Thread.Sleep(1000);
                }
                while (source.LastEventLatency > new TimeSpan(0, 0, 1));

                source.Stop();
                Assert.True(records.Count > 0, "There is an event after the timestamp.");
                Assert.True(records[0].Timestamp >= initialTimestamp && records[0].Timestamp < now, "There is an earlier event after the initial timestamp.");
                DateTime dateTime1 = records[records.Count - 1].Timestamp;

                //Write some new logs afte the source stop
                DateTime dateTime2 = DateTime.Now;
                string msg = $"Message generated by EvengLogTest {dateTime2}";
                int eventId = (int)(DateTime.Now.Ticks % ushort.MaxValue);
                EventLog.WriteEntry(logSource, msg, EventLogEntryType.Information, eventId);
                System.Threading.Thread.Sleep(1000);

                records.Clear();
                source.Start();
                System.Threading.Thread.Sleep(2000);
                //Should get the record when the souce is stopped
                Assert.True(records.Count > 0, "Should get the rew record.");
                Assert.True(records[0].Timestamp >= dateTime1, "Should pick up new records.");
            }
        }

        [Fact]
        public void TestInitialPositionBookMark()
        {
            string logName = "Application";
            string logSource = "Test";
            ListEventSink records = new ListEventSink();
            string sourceId = "TestInitialPositionBookMark";
            DeleteExistingBookmarkFile(sourceId);

            //This should generate a water mark file
            using (EventLogSource source = new EventLogSource(logName, null, new PluginContext(null, null, null)))
            {
                source.Subscribe(records);
                source.Id = sourceId;
                source.InitialPosition = InitialPositionEnum.Bookmark;
                source.Start();

                string msg = $"Message generated by EvengLogTest {DateTime.Now}";
                int eventId = (int)(DateTime.Now.Ticks % ushort.MaxValue);
                if (!EventLog.SourceExists(logSource))
                {
                    EventLog.CreateEventSource(logSource, logName);
                }
                EventLog.WriteEntry(logSource, msg, EventLogEntryType.Information, eventId);

                System.Threading.Thread.Sleep(1000);
                source.Stop();

                //Write some new logs afte the source stop
                DateTime dateTime2 = DateTime.Now;
                msg = $"Message generated by EvengLogTest {dateTime2}";
                EventLog.WriteEntry(logSource, msg, EventLogEntryType.Information, eventId);
                System.Threading.Thread.Sleep(1000);


                records.Clear();
                source.Start();
                System.Threading.Thread.Sleep(1000);
                //Should get the record when the souce is stopped
                Assert.True(records.Count > 0);
                Assert.True(records[0].Timestamp >= dateTime2);
            }
        }

        [Fact]
        public void TestNoEventData()
        {
            ListEventSink records = new ListEventSink();
            var config = TestUtility.GetConfig("Sources", "ApplicationLog");

            GenerateAndCaptureEvents(records, config);

            Assert.Null(((EventRecordEnvelope)records[0]).Data.EventData);
        }


        [Fact]
        public void TestEventData()
        {
            ListEventSink records = new ListEventSink();
            var config = TestUtility.GetConfig("Sources", "ApplicationLogWithEventData");

            GenerateAndCaptureEvents(records, config);

            Assert.True(((EventRecordEnvelope)records[0]).Data.EventData.Count > 0);
        }

        private static void DeleteExistingBookmarkFile(string sourceId)
        {
            string bookmarkFile = Path.Combine(Utility.GetKinesisTapProgramDataPath(), ConfigConstants.BOOKMARKS, $"{sourceId}.bm");
            if (File.Exists(bookmarkFile))
            {
                File.Delete(bookmarkFile);
            }
        }

        private static void GenerateAndCaptureEvents(ListEventSink records, IConfiguration config)
        {
            string logName = "Application";
            string logSource = "Test";
            using (EventLogSource source = new EventLogSource(logName, null, new PluginContext(config, null, null)))
            {
                source.Subscribe(records);
                source.Start();

                string msg = $"Message generated by EvengLogTest {DateTime.Now}";
                int eventId = (int)(DateTime.Now.Ticks % ushort.MaxValue);
                if (!EventLog.SourceExists(logSource))
                {
                    EventLog.CreateEventSource(logSource, logName);
                }
                EventLog.WriteEntry(logSource, msg, EventLogEntryType.Information, eventId);

                System.Threading.Thread.Sleep(1000);

                source.Stop();
            }
        }
    }
}
