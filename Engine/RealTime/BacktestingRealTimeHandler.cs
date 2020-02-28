﻿/*
 * QUANTCONNECT.COM - Democratizing Finance, Empowering Individuals.
 * Lean Algorithmic Trading Engine v2.0. Copyright 2014 QuantConnect Corporation.
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 *
*/

using System;
using System.Collections.Generic;
using System.Linq;
using QuantConnect.Interfaces;
using QuantConnect.Lean.Engine.Results;
using QuantConnect.Logging;
using QuantConnect.Packets;
using QuantConnect.Scheduling;
using QuantConnect.Util;

namespace QuantConnect.Lean.Engine.RealTime
{
    /// <summary>
    /// Pseudo realtime event processing for backtesting to simulate realtime events in fast forward.
    /// </summary>
    public class BacktestingRealTimeHandler : BaseRealTimeHandler, IRealTimeHandler
    {
        private bool _sortingScheduledEventsRequired;
        private IIsolatorLimitResultProvider _isolatorLimitProvider;
        private List<ScheduledEvent> _scheduledEventsSortedByTime = new List<ScheduledEvent>();

        /// <summary>
        /// Flag indicating the hander thread is completely finished and ready to dispose.
        /// this doesn't run as its own thread
        /// </summary>
        public bool IsActive => false;

        /// <summary>
        /// Initializes the real time handler for the specified algorithm and job
        /// </summary>
        public void Setup(IAlgorithm algorithm, AlgorithmNodePacket job, IResultHandler resultHandler, IApi api, IIsolatorLimitResultProvider isolatorLimitProvider)
        {
            //Initialize:
            Algorithm = algorithm;
            ResultHandler = resultHandler;
            _isolatorLimitProvider = isolatorLimitProvider;

            // create events for algorithm's end of tradeable dates
            // set up the events for each security to fire every tradeable date before market close
            base.Setup(Algorithm.StartDate, Algorithm.EndDate, job.Language);

            foreach (var scheduledEvent in GetScheduledEventsSortedByTime())
            {
                // zoom past old events
                scheduledEvent.SkipEventsUntil(algorithm.UtcTime);
                // set logging accordingly
                scheduledEvent.IsLoggingEnabled = Log.DebuggingEnabled;
            }
        }

        /// <summary>
        /// Normally this would run the realtime event monitoring. Backtesting is in fastforward so the realtime is linked to the backtest clock.
        /// This thread does nothing. Wait until the job is over.
        /// </summary>
        public void Run()
        {
        }

        /// <summary>
        /// Adds the specified event to the schedule
        /// </summary>
        /// <param name="scheduledEvent">The event to be scheduled, including the date/times the event fires and the callback</param>
        public override void Add(ScheduledEvent scheduledEvent)
        {
            if (Algorithm != null)
            {
                scheduledEvent.SkipEventsUntil(Algorithm.UtcTime);
            }

            ScheduledEvents.AddOrUpdate(scheduledEvent, GetScheduledEventUniqueId());

            if (Log.DebuggingEnabled)
            {
                scheduledEvent.IsLoggingEnabled = true;
            }

            _sortingScheduledEventsRequired = true;
        }

        /// <summary>
        /// Removes the specified event from the schedule
        /// </summary>
        /// <param name="scheduledEvent">The event to be removed</param>
        public override void Remove(ScheduledEvent scheduledEvent)
        {
            int id;
            ScheduledEvents.TryRemove(scheduledEvent, out id);

            _sortingScheduledEventsRequired = true;
        }

        /// <summary>
        /// Set the time for the realtime event handler.
        /// </summary>
        /// <param name="time">Current time.</param>
        public void SetTime(DateTime time)
        {
            var scheduledEvents = GetScheduledEventsSortedByTime();

            // the first element is always the next
            while (scheduledEvents.Count > 1 && scheduledEvents[0].NextEventUtcTime <= time)
            {
                _isolatorLimitProvider.Consume(scheduledEvents[0], time);

                LazySort(scheduledEvents);
            }
        }

        /// <summary>
        /// Scan for past events that didn't fire because there was no data at the scheduled time.
        /// </summary>
        /// <param name="time">Current time.</param>
        public void ScanPastEvents(DateTime time)
        {
            var scheduledEvents = GetScheduledEventsSortedByTime();

            // the first element is always the next
            while (scheduledEvents.Count > 1 && scheduledEvents[0].NextEventUtcTime < time)
            {
                var scheduledEvent = scheduledEvents[0];
                var nextEventUtcTime = scheduledEvent.NextEventUtcTime;

                Algorithm.SetDateTime(nextEventUtcTime);

                try
                {
                    _isolatorLimitProvider.Consume(scheduledEvent, nextEventUtcTime);
                }
                catch (ScheduledEventException scheduledEventException)
                {
                    var errorMessage = $"BacktestingRealTimeHandler.Run(): There was an error in a scheduled event {scheduledEvent.Name}. The error was {scheduledEventException.Message}";

                    Log.Error(scheduledEventException, errorMessage);

                    ResultHandler.RuntimeError(errorMessage);

                    // Errors in scheduled event should be treated as runtime error
                    // Runtime errors should end Lean execution
                    Algorithm.RunTimeError = scheduledEventException;
                }

                LazySort(scheduledEvents);
            }
        }

        /// <summary>
        /// Stop the real time thread
        /// </summary>
        public void Exit()
        {
            // this doesn't run as it's own thread, so nothing to exit
        }

        private List<ScheduledEvent> GetScheduledEventsSortedByTime()
        {
            if (_sortingScheduledEventsRequired)
            {
                _sortingScheduledEventsRequired = false;
                _scheduledEventsSortedByTime = ScheduledEvents
                    // we order by next event time
                    .OrderBy(x => x.Key.NextEventUtcTime)
                    // then by unique id so that for scheduled events in the same time
                    // respect their creation order, so its deterministic
                    .ThenBy(x => x.Value)
                    .Select(x => x.Key).ToList();
            }

            return _scheduledEventsSortedByTime;
        }

        /// <summary>
        /// Sorts the first element of the provided list and supposes the rest of the collection is sorted.
        /// Supposes the collection has at least 1 element
        /// </summary>
        public static void LazySort(IList<ScheduledEvent> scheduledEvents)
        {
            var scheduledEvent = scheduledEvents[0];
            var nextEventUtcTime = scheduledEvent.NextEventUtcTime;

            if (scheduledEvents.Count > 1
                // if our NextEventUtcTime is after the next event we sort our selves
                && nextEventUtcTime > scheduledEvents[1].NextEventUtcTime)
            {
                // remove ourselves and re insert at the correct position, the rest of the items are sorted!
                scheduledEvents.RemoveAt(0);

                var position = scheduledEvents.BinarySearch(nextEventUtcTime,
                    (time, orderEvent) => time.CompareTo(orderEvent.NextEventUtcTime));
                if (position >= 0)
                {
                    // Calling insert isn't that performance but note that we are doing it once
                    // and has better performance than sorting the entire collection
                    scheduledEvents.Insert(position, scheduledEvent);
                }
                else
                {
                    var index = ~position;
                    if (index == scheduledEvents.Count)
                    {
                        // bigger than all of them insert in the end
                        scheduledEvents.Add(scheduledEvent);
                    }
                    else
                    {
                        scheduledEvents.Insert(index, scheduledEvent);
                    }
                }
            }
        }
    }
}