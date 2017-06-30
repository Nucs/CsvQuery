﻿using System;
using System.Data.SqlTypes;
using System.Diagnostics;

namespace CsvQuery.Csv
{
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using Tools;

    public class CsvAnalyzer
    {
        private class Stat
        {
            public int Occurances;
            public float Variance;
        }

        /// <summary>
        /// Analyzes a CSV text and tries to figure out separators, quote chars etc
        /// </summary>
        /// <param name="csvString"></param>
        /// <returns></returns>
        public static CsvSettings Analyze(string csvString)
        {
            // TODO: strings with quoted values (e.g. 'hej,san')
            // Not sure how to detect this, but we could just run the variance analysis
            // 3 times, one for none, one for ' and one for " and see which has best variances
            // That wouldn't detect escape chars though, or odd variants like [this]

            // First do a letter frequency analysis on each row
            var s = new StringReader(csvString);
            string line;
            int lineCount = 0;
            var frequencies = new List<Dictionary<char, int>>();
            var occurrences = new Dictionary<char, int>();
            var wordStarts = new Dictionary<int, int>();
            var bigSpaces = new Dictionary<int, int>();

            while ((line = s.ReadLine()) != null)
            {
                var letterFrequency = new Dictionary<char, int>();
                int spaces = 0, i = 0;
                foreach (var c in line)
                {
                    letterFrequency.Increase(c);
                    occurrences.Increase(c);

                    if (c == ' ')
                    {
                        if (++spaces >= 2) bigSpaces.Increase(i);
                    }
                    else
                    {
                        if (spaces >= 2) wordStarts.Increase(i);
                        spaces = 0;
                    }
                    i++;
                }

                frequencies.Add(letterFrequency);
                if (lineCount++ > 20) break;
            }

            // Then check the variance on the frequency of each char
            var variances = new Dictionary<char, float>();
            foreach (var c in occurrences.Keys)
            {
                var mean = (float) occurrences[c] / lineCount;
                float variance = 0;
                foreach (var frequency in frequencies)
                {
                    var f = 0;
                    if (frequency.ContainsKey(c)) f = frequency[c];
                    variance += (f - mean) * (f - mean);
                }
                variance /= lineCount;
                variances.Add(c, variance);
            }

            // The char with lowest variance is most likely the separator
            var result = new CsvSettings {Separator = GetSeparatorFromVariance(variances, occurrences, lineCount)};
            if (result.Separator != default(char)) return result;
            
            // Failed to detect separator. Could it be a fixed-width file?
            var commonSpace = bigSpaces.Where(x => x.Value == lineCount).Select(x => x.Key).OrderBy(x => x);
            var lastvalue = 0;
            int lastStart = 0;
            var foundfieldWidths = new List<int>();
            foreach (var space in commonSpace)
            {
                if (space != lastvalue + 1)
                {
                    foundfieldWidths.Add(space - lastStart);
                    lastStart = space;
                }

                lastvalue = space;
            }
            if (foundfieldWidths.Count < 3) return result; // unlikely fixed width
            foundfieldWidths.Add(-1); // Last column gets "the rest"
            result.FieldWidths = foundfieldWidths;
            return result;
        }

        private static Dictionary<char, Stat> CalcVariances(string csvString, char textQualifyer, char escapeChar)
        {
            var s = new StringReader(csvString);
            string line;
            int lineCount = 0;
            var statistics = new Dictionary<char, Stat>();
            var frequencies = new List<Dictionary<char, int>>();
            while ((line = s.ReadLine()) != null)
            {
                var letterFrequency = new Dictionary<char, int>();
                foreach (var c in line)
                {
                    if (!statistics.ContainsKey(c))
                        statistics.Add(c, new Stat{Occurances = 1});
                    else
                        statistics[c].Occurances++;

                    if (!letterFrequency.ContainsKey(c))
                        letterFrequency.Add(c, 1);
                    else
                        letterFrequency[c]++;
                }

                frequencies.Add(letterFrequency);
                if (lineCount++ > 20) break;
            }

            // Then check the variance on the frequency of each char
            foreach (var c in statistics.Keys)
            {
                var mean = (float)statistics[c].Occurances / lineCount;
                float variance = 0;
                foreach (var frequency in frequencies)
                {
                    var f = 0;
                    if (frequency.ContainsKey(c)) f = frequency[c];
                    variance += (f - mean) * (f - mean);
                }
                variance /= lineCount;
                statistics[c].Variance = variance;
            }

            return statistics;
        }

        private static char GetSeparatorFromVariance(Dictionary<char, float> variances, Dictionary<char, int> occurrences, int lineCount)
        {
            var preferredSeparators = Main.Settings.Separators.Replace("\\t", "\t");

            // The char with lowest variance is most likely the separator
            // Optimistic: check prefered with 0 variance 
            var separator = variances
                .Where(x => x.Value == 0f && preferredSeparators.IndexOf(x.Key) != -1)
                .OrderByDescending(x => occurrences[x.Key])
                .Select(x => (char?)x.Key)
                .FirstOrDefault();

            if (separator != null) 
                return separator.Value;

            var defaultKV = default(KeyValuePair<char, float>);

            // Ok, no perfect separator. Check if the best char that exists on all lines is a prefered separator
            var sortedVariances = variances.OrderBy(x => x.Value).ToList();
            var best = sortedVariances.FirstOrDefault(x => occurrences[x.Key] >= lineCount);
            if (!best.Equals(defaultKV) && preferredSeparators.IndexOf(best.Key) != -1) 
                return best.Key;

            // No? Second best?
            best = sortedVariances.Where(x => occurrences[x.Key] >= lineCount).Skip(1).FirstOrDefault();
            if (!best.Equals(defaultKV) && preferredSeparators.IndexOf(best.Key) != -1)
                return best.Key;

            // Ok, screw the preferred separators, is any other char a perfect separator? (and common, i.e. at least 3 per line)
            separator = variances
                .Where(x => x.Value == 0f && occurrences[x.Key] >= lineCount*2)
                .OrderByDescending(x => occurrences[x.Key])
                .Select(x => (char?)x.Key)
                .FirstOrDefault();
            if (separator != null)
                return separator.Value;
            
            // Ok, I have no idea
            return '\0';
        }

        public static CsvColumnTypes DetectColumnTypes(List<string[]> data, bool? hasHeader)
        {
            return new CsvColumnTypes(data, hasHeader);
        }
    }

    public class CsvColumnTypes
    {
        public bool HasHeader { get; set; }
        public List<CsvColumnType> Columns { get; set; }
        

        public CsvColumnTypes(List<string[]> data, bool? hasHeader)
        {
            Columns = new List<CsvColumnType>();
            var headerTypes = new List<CsvColumnType>();
            var rowLengths = new Dictionary<int, int>();
            var columns = data[0].Length;
            bool first = true, allStrings = true;
            foreach (var cols in data)
            {
                rowLengths.Increase(cols.Length);
                if (first && (!hasHeader.HasValue || hasHeader.Value))
                {
                    // Save to headerTypes
                    foreach (var col in cols)
                    {
                        var headerType = new CsvColumnType(col);
                        headerTypes.Add(headerType);
                        if (headerType.DataType != ColumnType.String) allStrings = false;
                    }
                }
                else
                {
                    // Save to Columns
                    int i = 0;
                    foreach (var col in cols)
                    {
                        var columnType = new CsvColumnType(col);
                        if (Columns.Count <= i) Columns.Add(columnType);
                        else Columns[i].Update(columnType);
                        i++;
                    }
                }

                if (first)
                    first = false;
            }

            // If the first row is all strings, but the data rows have numbers, it's probably a header
            HasHeader = hasHeader ?? allStrings && Columns.Any(x => x.DataType != ColumnType.String);
            Trace.TraceInformation($"Header row analysis: User set={hasHeader.HasValue}, First row all strings:{allStrings}\n\tData columns strings: {Columns.Count(x => x.DataType == ColumnType.String)}/{Columns.Count}\n\rHeader row: {HasHeader}");

            if (!hasHeader.HasValue && HasHeader == false)
            {
                // We _detected_ that the file has no headers, so the headerTypes needs to be merged into the other types
                for (int c = 0; c < headerTypes.Count; c++)
                {
                    if (Columns.Count <= c) Columns.Add(headerTypes[c]);
                    else Columns[c].Update(headerTypes[c]);
                }
            }

            if (rowLengths.Count > 1)
            {
                Trace.TraceWarning("Column count mismatch:" + string.Join(",", rowLengths.Select(p => $"{p.Value} rows had {p.Key} columns")));
            }
        }

        public class CsvColumnType
        {
            public ColumnType DataType;
            public int Size;
            public bool Nullable;

            /// <summary>
            /// Detect data type from string
            /// </summary>
            /// <param name="csvText"></param>
            public CsvColumnType(string csvText)
            {
                if (string.IsNullOrWhiteSpace(csvText))
                {
                    DataType = ColumnType.Empty;
                    Nullable = true;
                    return;
                }
                Size = csvText.Length;
                if (long.TryParse(csvText, out long iout))
                {
                    if (!Main.Settings.ConvertInitialZerosToNumber && csvText.StartsWith("0")
                        || Main.Settings.MaxIntegerStringLength < csvText.Length)
                        DataType = ColumnType.String;
                    else
                        DataType = ColumnType.Integer;
                }
                else if (double.TryParse(csvText, out double d))
                    DataType = ColumnType.Real;
                else
                    DataType = ColumnType.String;
            }

            /// <summary>
            /// Updates a type with a new value - it becomes the most generic of the two
            /// </summary>
            /// <param name="csvType"></param>
            public void Update(string csvType)
            {
                Update(new CsvColumnType(csvType));
            }

            public void Update(CsvColumnType csvType)
            {
                DataType = csvType.DataType > DataType ? csvType.DataType : DataType;
                Size = Math.Max(Size, csvType.Size);
                Nullable = Nullable || csvType.Nullable;
            }
        }

        public enum ColumnType
        {
            Empty=0,
            Integer=1,
            Real=2,
            String=4
        }
    }
}
