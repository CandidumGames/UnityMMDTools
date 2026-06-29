using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using UnityEngine;

namespace UMT
{
    /// <summary>
    /// Timing and profiling helpers for measuring elapsed durations of import/conversion stages.
    /// </summary>
    public static class UMTTiming
    {
        /// <summary>
        /// A disposable measurement scope that reports the elapsed time of a labeled block to a callback on disposal.
        /// </summary>
        public readonly struct Scope : IDisposable
        {
            private readonly Action<string, TimeSpan> m_Callback;
            private readonly string m_Label;
            private readonly long m_StartTimestamp;

            /// <summary>
            /// Creates a measurement scope that records the start timestamp when a callback is provided.
            /// </summary>
            /// <param name="callback">The callback invoked with the label and elapsed time on disposal; measurement is skipped when <c>null</c>.</param>
            /// <param name="label">The label identifying the measured block.</param>
            internal Scope(Action<string, TimeSpan> callback, string label)
            {
                m_Callback = callback;
                m_Label = label;
                m_StartTimestamp = callback != null ? Stopwatch.GetTimestamp() : 0L;
            }

            /// <summary>
            /// Computes the elapsed time since construction and invokes the callback, unless no callback was supplied.
            /// </summary>
            public void Dispose()
            {
                if (m_Callback == null)
                {
                    return;
                }

                long elapsedTicks = Stopwatch.GetTimestamp() - m_StartTimestamp;
                TimeSpan elapsed = TimeSpan.FromSeconds(elapsedTicks / (double)Stopwatch.Frequency);
                m_Callback(m_Label, elapsed);
            }
        }

        /// <summary>
        /// Begins a timing scope for the given label, reporting the elapsed duration to the callback when disposed.
        /// </summary>
        /// <param name="callback">The callback invoked with the label and elapsed time on disposal; measurement is skipped when <c>null</c>.</param>
        /// <param name="label">The label identifying the measured block.</param>
        /// <returns>A disposable scope that reports timing on disposal.</returns>
        public static Scope Measure(Action<string, TimeSpan> callback, string label)
        {
            return new Scope(callback, label);
        }

        /// <summary>
        /// Formats an elapsed duration as a millisecond string (for example, <c>"12.345 ms"</c>).
        /// </summary>
        /// <param name="elapsed">The elapsed duration to format.</param>
        /// <returns>The formatted millisecond string.</returns>
        public static string FormatElapsed(TimeSpan elapsed)
        {
            return $"{elapsed.TotalMilliseconds:0.###} ms";
        }
    }

    /// <summary>
    /// Accumulates labeled timing records and builds a human-readable timing report.
    /// </summary>
    public sealed class UMTTimingCollector
    {
        /// <summary>
        /// A single labeled timing measurement.
        /// </summary>
        public readonly struct Record
        {
            /// <summary>
            /// The label identifying the measured block.
            /// </summary>
            public readonly string label;

            /// <summary>
            /// The elapsed duration of the measured block.
            /// </summary>
            public readonly TimeSpan elapsed;

            /// <summary>
            /// Creates a timing record.
            /// </summary>
            /// <param name="label">The label identifying the measured block.</param>
            /// <param name="elapsed">The elapsed duration of the measured block.</param>
            public Record(string label, TimeSpan elapsed)
            {
                this.label = label;
                this.elapsed = elapsed;
            }
        }

        private readonly List<Record> m_Records = new List<Record>();

        /// <summary>
        /// Gets the collected timing records in the order they were recorded.
        /// </summary>
        public IReadOnlyList<Record> records => m_Records;

        /// <summary>
        /// Gets the sum of all recorded elapsed durations.
        /// </summary>
        public TimeSpan totalElapsed
        {
            get
            {
                TimeSpan total = TimeSpan.Zero;
                for (int i = 0; i < m_Records.Count; ++i)
                {
                    total += m_Records[i].elapsed;
                }

                return total;
            }
        }

        /// <summary>
        /// Appends a new labeled timing record to the collector.
        /// </summary>
        /// <param name="label">The label identifying the measured block.</param>
        /// <param name="elapsed">The elapsed duration of the measured block.</param>
        public void RecordTiming(string label, TimeSpan elapsed)
        {
            m_Records.Add(new Record(label, elapsed));
        }

        /// <summary>
        /// Builds a multi-line report showing the total elapsed time followed by each recorded label and duration.
        /// </summary>
        /// <param name="totalLabel">The label used for the total line.</param>
        /// <returns>The formatted timing report.</returns>
        public string BuildReport(string totalLabel = "Total")
        {
            StringBuilder builder = new StringBuilder();
            builder.Append(totalLabel);
            builder.Append(": ");
            builder.Append(UMTTiming.FormatElapsed(totalElapsed));

            for (int i = 0; i < m_Records.Count; ++i)
            {
                Record record = m_Records[i];
                builder.AppendLine();
                builder.Append(record.label);
                builder.Append(": ");
                builder.Append(UMTTiming.FormatElapsed(record.elapsed));
            }

            return builder.ToString();
        }
    }

    /// <summary>
    /// A frame budget that yields control back to the Unity main thread when the elapsed time exceeds a specified threshold, allowing for responsive UI and preventing long frame stalls during heavy processing.
    /// </summary>
    public sealed class UMTFrameBudget
    {
        private readonly double m_BudgetMs;
        private readonly Stopwatch m_Stopwatch = new();

        /// <summary>
        /// Initializes a new instance of the <see cref="UMTFrameBudget"/> class with the specified time budget in milliseconds.
        /// </summary>
        /// <param name="budgetMs">The time budget in milliseconds. If the elapsed time
        /// exceeds this value, the next call to <see cref="YieldIfNeeded"/> will yield control back to the Unity main thread.</param>
        public UMTFrameBudget(double budgetMs)
        {
            m_BudgetMs = budgetMs;
            m_Stopwatch.Start();
        }

        /// <summary>
        /// Yields control back to the Unity main thread if the elapsed time exceeds the specified budget.
        /// </summary>
        public async Awaitable YieldIfNeeded()
        {
            if (m_Stopwatch.Elapsed.TotalMilliseconds < m_BudgetMs)
            {
                return;
            }

            await Awaitable.NextFrameAsync();
            m_Stopwatch.Restart();
        }
    }
}