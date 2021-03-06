﻿/*
MIT License

Copyright (c) 2012-present Digital Ruby, LLC - https://www.digitalruby.com

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
*/

#region Imports

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Serialization;

#endregion Imports

namespace DigitalRuby.IPBanCore
{
    /// <summary>
    /// Configuration for ip ban app
    /// </summary>
    public class IPBanConfig : IIsWhitelisted
    {
        /// <summary>
        /// Allow temporary change of config
        /// </summary>
        public class TempConfigChanger : IDisposable
        {
            private readonly IConfigReaderWriter config;
            private readonly string origConfig;
            private readonly string modifiedConfig;

            /// <summary>
            /// Constructor
            /// </summary>
            /// <param name="service">Service</param>
            /// <param name="modifier">Config modifier</param>
            /// <param name="modifiedConfig">Receives modified config</param>
            public TempConfigChanger(IConfigReaderWriter config, Func<string, string> modifier) :
                this(config, modifier, out _)
            {
            }

            /// <summary>
            /// Constructor
            /// </summary>
            /// <param name="service">Service</param>
            /// <param name="modifier">Config modifier</param>
            /// <param name="modifiedConfig">Receives modified config</param>
            public TempConfigChanger(IConfigReaderWriter config, Func<string, string> modifier, out string modifiedConfig)
            {
                this.config = config;
                origConfig = config.ReadConfigAsync().Sync();
                this.modifiedConfig = modifiedConfig = modifier(origConfig);
                config.WriteConfigAsync(this.modifiedConfig).Sync();
                if (config is IIPBanService service)
                {
                    service.RunCycle().Sync();
                }
            }

            /// <summary>
            /// Revert config back to original value
            /// </summary>
            public void Dispose()
            {
                config.WriteConfigAsync(origConfig).Sync();
            }

            /// <summary>
            /// Get the modified config
            /// </summary>
            public string ModifiedConfig => modifiedConfig;
        }

        /// <summary>
        /// Default config file name
        /// </summary>
        public const string DefaultFileName = "ipban.config";

        private static readonly HashSet<string> ignoreListEntries = new HashSet<string>
        {
            "0.0.0.0", "::0", "127.0.0.1", "::1", "localhost"
        };

        private static readonly TimeSpan[] emptyTimeSpanArray = new TimeSpan[] { TimeSpan.Zero };
        private static readonly IPBanLogFileToParse[] emptyLogFilesToParseArray = new IPBanLogFileToParse[0];
        private static readonly TimeSpan maxBanTimeSpan = TimeSpan.FromDays(90.0);

        private readonly Dictionary<string, string> appSettings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly IPBanLogFileToParse[] logFiles;
        private readonly TimeSpan[] banTimes = new TimeSpan[] { TimeSpan.FromDays(1.0d) };
        private readonly TimeSpan expireTime = TimeSpan.FromDays(1.0d);
        private readonly TimeSpan cycleTime = TimeSpan.FromMinutes(1.0d);
        private readonly TimeSpan minimumTimeBetweenFailedLoginAttempts = TimeSpan.FromSeconds(5.0);
        private readonly TimeSpan minimumTimeBetweenSuccessfulLoginAttempts = TimeSpan.FromSeconds(5.0);
        private readonly int failedLoginAttemptsBeforeBan = 5;
        private readonly bool resetFailedLoginCountForUnbannedIPAddresses;
        private readonly string firewallRulePrefix = "IPBan_";

        // black list data structures
        private readonly HashSet<System.Net.IPAddress> blackList = new HashSet<System.Net.IPAddress>();
        private readonly Regex blackListRegex;
        private readonly HashSet<IPAddressRange> blackListRanges = new HashSet<IPAddressRange>();
        private readonly HashSet<string> blackListOther = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // white list data structures
        private readonly HashSet<System.Net.IPAddress> whitelist = new HashSet<System.Net.IPAddress>();
        private readonly Regex whitelistRegex;
        private readonly HashSet<IPAddressRange> whitelistRanges = new HashSet<IPAddressRange>();
        private readonly HashSet<string> whitelistOther = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private readonly bool clearBannedIPAddressesOnRestart;
        private readonly bool clearFailedLoginsOnSuccessfulLogin;
        private readonly HashSet<string> userNameWhitelist = new HashSet<string>(StringComparer.Ordinal);
        private readonly int userNameWhitelistMaximumEditDistance = 2;
        private readonly Regex userNameWhitelistRegex;
        private readonly int failedLoginAttemptsBeforeBanUserNameWhitelist = 20;
        private readonly string processToRunOnBan;
        private readonly bool useDefaultBannedIPAddressHandler;
        private readonly string getUrlUpdate;
        private readonly string getUrlStart;
        private readonly string getUrlStop;
        private readonly string getUrlConfig;
        private readonly string externalIPAddressUrl;
        private readonly string firewallUriRules;
        private readonly IDnsLookup dns;
        private readonly IDnsServerList dnsList;
        private readonly List<IPBanFirewallRule> extraRules = new List<IPBanFirewallRule>();
        private readonly EventViewerExpressionsToBlock expressionsFailure;
        private readonly EventViewerExpressionsToNotify expressionsSuccess;

        /// <summary>
        /// Static constructor - migrate config file from DigitalRuby.IPBan.dll.config to ipban.config
        /// </summary>
        static IPBanConfig()
        {
            string oldConfigPath = null;
            string newConfigPath = null;

            try
            {
                // move DigitalRuby.IPBan.dll.config to ipban.config
                oldConfigPath = Path.Combine(AppContext.BaseDirectory, "DigitalRuby.IPBan.dll.config");
                newConfigPath = Path.Combine(AppContext.BaseDirectory, "ipban.config");
                if (File.Exists(oldConfigPath))
                {
                    ExtensionMethods.Retry(() =>
                    {
                        File.Copy(oldConfigPath, newConfigPath, true);
                        File.Delete(oldConfigPath);
                    });
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Failed to copy old config file at {0} to new config file at {1}",
                    oldConfigPath, newConfigPath);
            }
        }

        private IPBanConfig(string xml, IDnsLookup dns = null, IDnsServerList dnsList = null)
        {
            this.dns = dns ?? DefaultDnsLookup.Instance;
            this.dnsList = dnsList;

            // deserialize with XmlDocument, the .net core Configuration class is quite buggy
            XmlDocument doc = new XmlDocument();
            doc.LoadXml(xml);
            foreach (XmlNode node in doc.SelectNodes("//appSettings/add"))
            {
                appSettings[node.Attributes["key"].Value] = node.Attributes["value"].Value;
            }

            GetConfig<int>("FailedLoginAttemptsBeforeBan", ref failedLoginAttemptsBeforeBan, 1, 50);
            GetConfig<bool>("ResetFailedLoginCountForUnbannedIPAddresses", ref resetFailedLoginCountForUnbannedIPAddresses);
            GetConfigArray<TimeSpan>("BanTime", ref banTimes, emptyTimeSpanArray);
            for (int i = 0; i < banTimes.Length; i++)
            {
                // according to documentation, a ban time of 0 should become max ban time
                if (banTimes[i].Ticks <= 0)
                {
                    banTimes[i] = maxBanTimeSpan;
                }
                else
                {
                    banTimes[i] = banTimes[i].Clamp(TimeSpan.FromMinutes(1.0), maxBanTimeSpan);
                }
            }
            GetConfig<bool>("ClearBannedIPAddressesOnRestart", ref clearBannedIPAddressesOnRestart);
            GetConfig<bool>("ClearFailedLoginsOnSuccessfulLogin", ref clearFailedLoginsOnSuccessfulLogin);
            GetConfig<TimeSpan>("ExpireTime", ref expireTime, TimeSpan.Zero, maxBanTimeSpan);
            if (expireTime.TotalMinutes < 1.0)
            {
                expireTime = maxBanTimeSpan;
            }
            GetConfig<TimeSpan>("CycleTime", ref cycleTime, TimeSpan.FromSeconds(5.0), TimeSpan.FromMinutes(1.0), false);
            GetConfig<TimeSpan>("MinimumTimeBetweenFailedLoginAttempts", ref minimumTimeBetweenFailedLoginAttempts, TimeSpan.Zero, TimeSpan.FromSeconds(15.0), false);
            GetConfig<string>("FirewallRulePrefix", ref firewallRulePrefix);

            string whitelistString = GetConfig<string>("Whitelist", string.Empty);
            string whitelistRegexString = GetConfig<string>("WhitelistRegex", string.Empty);
            string blacklistString = GetConfig<string>("Blacklist", string.Empty);
            string blacklistRegexString = GetConfig<string>("BlacklistRegex", string.Empty);
            PopulateList(whitelist, whitelistRanges, whitelistOther, ref whitelistRegex, whitelistString, whitelistRegexString);
            PopulateList(blackList, blackListRanges, blackListOther, ref blackListRegex, blacklistString, blacklistRegexString);
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                XmlNode node = doc.SelectSingleNode("//ExpressionsToBlock");
                if (node != null)
                {
                    expressionsFailure = new XmlSerializer(typeof(EventViewerExpressionsToBlock)).Deserialize(new XmlNodeReader(node)) as EventViewerExpressionsToBlock;
                    if (expressionsFailure != null)
                    {
                        foreach (EventViewerExpressionGroup group in expressionsFailure.Groups)
                        {
                            foreach (EventViewerExpression expression in group.Expressions)
                            {
                                expression.Regex = (expression.Regex?.ToString() ?? string.Empty).Trim();
                            }
                        }
                    }
                }
                node = doc.SelectSingleNode("//ExpressionsToNotify");
                if (node != null)
                {
                    expressionsSuccess = new XmlSerializer(typeof(EventViewerExpressionsToNotify)).Deserialize(new XmlNodeReader(node)) as EventViewerExpressionsToNotify;
                    if (expressionsSuccess != null)
                    {
                        foreach (EventViewerExpressionGroup group in expressionsSuccess.Groups)
                        {
                            group.NotifyOnly = true;
                            foreach (EventViewerExpression expression in group.Expressions)
                            {
                                expression.Regex = (expression.Regex?.ToString() ?? string.Empty).Trim();
                            }
                        }
                    }
                }
            }
            else
            {
                expressionsFailure = new EventViewerExpressionsToBlock();
                expressionsSuccess = new EventViewerExpressionsToNotify();
            }
            try
            {
                if (new XmlSerializer(typeof(IPBanLogFilesToParse)).Deserialize(new XmlNodeReader(doc.SelectSingleNode("//LogFilesToParse"))) is IPBanLogFilesToParse logFilesToParse)
                {
                    logFiles = logFilesToParse.LogFiles;
                }
                else
                {
                    logFiles = emptyLogFilesToParseArray;
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex);
                logFiles = new IPBanLogFileToParse[0];
            }
            GetConfig<string>("ProcessToRunOnBan", ref processToRunOnBan);
            GetConfig<bool>("UseDefaultBannedIPAddressHandler", ref useDefaultBannedIPAddressHandler);

            string userNameWhitelistString = GetConfig<string>("UserNameWhitelist", string.Empty);
            if (!string.IsNullOrEmpty(userNameWhitelistString))
            {
                foreach (string userName in userNameWhitelistString.Split(','))
                {
                    string userNameTrimmed = userName.Normalize().ToUpperInvariant().Trim();
                    userNameWhitelist.Add(userNameTrimmed);
                }
            }
            string userNameWhitelistRegexString = GetConfig<string>("UserNameWhitelistRegex", string.Empty);
            if (!string.IsNullOrWhiteSpace(userNameWhitelistRegexString))
            {
                userNameWhitelistRegex = new Regex(userNameWhitelistRegexString, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline);
            }
            GetConfig<int>("UserNameWhitelistMinimumEditDistance", ref userNameWhitelistMaximumEditDistance);
            GetConfig<int>("FailedLoginAttemptsBeforeBanUserNameWhitelist", ref failedLoginAttemptsBeforeBanUserNameWhitelist);
            GetConfig<string>("GetUrlUpdate", ref getUrlUpdate);
            GetConfig<string>("GetUrlStart", ref getUrlStart);
            GetConfig<string>("GetUrlStop", ref getUrlStop);
            GetConfig<string>("GetUrlConfig", ref getUrlConfig);
            GetConfig<string>("ExternalIPAddressUrl", ref externalIPAddressUrl);
            GetConfig<string>("FirewallUriRules", ref firewallUriRules);
            if (string.IsNullOrWhiteSpace(firewallUriRules))
            {
                // legacy
                GetConfig<string>("FirewallUriSources", ref firewallUriRules);
            }
            firewallUriRules = (firewallUriRules ?? string.Empty).Trim();

            // parse firewall block rules, one per line
            ParseFirewallBlockRules();
        }

        private bool IsMatch(string entry, System.Net.IPAddress entryIPAddress, HashSet<System.Net.IPAddress> set, HashSet<IPAddressRange> ranges, HashSet<string> others, Regex regex)
        {
            if (!string.IsNullOrWhiteSpace(entry))
            {
                entry = entry.Trim().Normalize();
                if (entryIPAddress != null || System.Net.IPAddress.TryParse(entry, out entryIPAddress))
                {
                    // direct ip match in set or match in range of ip address list
                    if (set.Contains(entryIPAddress) || ranges.Any(r => r.Contains(entryIPAddress)))
                    {
                        return true;
                    }
                }
                else if (others.Contains(entry))
                {
                    // direct string match in other set
                    return true;
                }

                // fallback to regex match
                if (!(regex is null))
                {
                    // try the regex as last resort
                    return regex.IsMatch(entry);
                }
            }

            return false;
        }

        private void PopulateList(HashSet<System.Net.IPAddress> set,
            HashSet<IPAddressRange> ranges,
            HashSet<string> others,
            ref Regex regex,
            string setValue,
            string regexValue)
        {
            setValue = (setValue ?? string.Empty).Trim();
            regexValue = (regexValue ?? string.Empty).Replace("*", @"[0-9A-Fa-f:]+?").Trim();
            set.Clear();
            regex = null;

            if (!string.IsNullOrWhiteSpace(setValue))
            {
                foreach (string entry in setValue.Split(new char[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(e => e.Trim()))
                {
                    string entryWithoutComment = entry;
                    int pos = entryWithoutComment.IndexOf('?');
                    if (pos >= 0)
                    {
                        entryWithoutComment = entryWithoutComment.Substring(0, pos);
                    }
                    entryWithoutComment = entryWithoutComment.Trim();
                    if (!ignoreListEntries.Contains(entryWithoutComment))
                    {
                        if (IPAddressRange.TryParse(entryWithoutComment, out IPAddressRange range))
                        {
                            if (range.Begin.Equals(range.End))
                            {
                                set.Add(range.Begin);
                            }
                            else
                            {
                                ranges.Add(range);
                            }
                        }
                        else if (Uri.CheckHostName(entryWithoutComment) != UriHostNameType.Unknown)
                        {
                            try
                            {
                                // add entries for each ip address that matches the dns entry
                                IPAddress[] addresses = null;
                                ExtensionMethods.Retry(() => addresses = dns.GetHostAddressesAsync(entryWithoutComment).Sync());
                                foreach (IPAddress adr in addresses)
                                {
                                    set.Add(adr);
                                }
                            }
                            catch (Exception ex)
                            {
                                Logger.Error(ex, "Unable to resolve dns for {0}", entryWithoutComment);

                                // eat exception, nothing we can do
                                others.Add(entryWithoutComment);
                            }
                        }
                        else
                        {
                            others.Add(entryWithoutComment);
                        }
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(regexValue))
            {
                regex = ParseRegex(regexValue);
            }
        }

        private void ParseFirewallBlockRules()
        {
            string firewallBlockRuleString = null;
            GetConfig<string>("FirewallRules", ref firewallBlockRuleString);
            firewallBlockRuleString = (firewallBlockRuleString ?? string.Empty).Trim();
            if (firewallBlockRuleString.Length == 0)
            {
                return;
            }
            IEnumerable<string> firewallBlockRuleList = firewallBlockRuleString.Trim().Split('\n').Select(s => s.Trim()).Where(s => s.Length != 0);
            foreach (string firewallBlockRule in firewallBlockRuleList)
            {
                string[] pieces = firewallBlockRule.Split(';');
                if (pieces.Length == 5)
                {
                    IPBanFirewallRule firewallBlockRuleObj = new IPBanFirewallRule
                    {
                        Block = (pieces[1].Equals("block", StringComparison.OrdinalIgnoreCase)),
                        IPAddressRanges = pieces[2].Split(',').Select(p => IPAddressRange.Parse(p)).ToList(),
                        Name = "EXTRA_" + pieces[0].Trim(),
                        AllowPortRanges = pieces[3].Split(',').Select(p => PortRange.Parse(p)).Where(p => p.MinPort >= 0).ToList(),
                        PlatformRegex = new Regex(pieces[4].Replace('*', '.'), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
                    };
                    if (firewallBlockRuleObj.PlatformRegex.IsMatch(OSUtility.Instance.Name))
                    {
                        extraRules.Add(firewallBlockRuleObj);
                    }
                }
                else
                {
                    Logger.Warn("Firewall block rule entry should have 3 comma separated pieces: name;ips;ports. Invalid entry: {0}", firewallBlockRule);
                }
            }
        }

        /// <summary>
        /// Validate a regex - returns an error otherwise empty string if success
        /// </summary>
        /// <param name="regex">Regex to validate, can be null or empty</param>
        /// <param name="options">Regex options</param>
        /// <param name="throwException">True to throw the exception instead of returning the string, false otherwise</param>
        /// <returns>Null if success, otherwise an error string indicating the problem</returns>
        public static string ValidateRegex(string regex, RegexOptions options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, bool throwException = false)
        {
            try
            {
                if (regex != null)
                {
                    new Regex(regex, options);
                }
                return null;
            }
            catch (Exception ex)
            {
                if (throwException)
                {
                    throw;
                }
                return ex.Message;
            }
        }

        private static readonly ConcurrentDictionary<string, Regex> regexCache = new ConcurrentDictionary<string, Regex>();
        /// <summary>
        /// Get a regex from text
        /// </summary>
        /// <param name="text">Text</param>
        /// <param name="multiline">Whether to use multi-line regex, default is false which is single line</param>
        /// <returns>Regex or null if text is null or whitespace</returns>
        public static Regex ParseRegex(string text, bool multiline = false)
        {
            text = (text ?? string.Empty).Trim();
            if (text.Length == 0)
            {
                return null;
            }

            string[] lines = text.Split('\n');
            StringBuilder sb = new StringBuilder();
            foreach (string line in lines)
            {
                string trimmedLine = line.Trim();
                if (trimmedLine.Length != 0)
                {
                    sb.Append(trimmedLine);
                }
            }
            RegexOptions options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled;
            if (multiline)
            {
                options |= RegexOptions.Multiline;
            }
            string sbText = sb.ToString();
            string cacheKey = ((uint)options).ToString("X8") + ":" + sbText;
            return regexCache.GetOrAdd(cacheKey, _key => new Regex(sbText, options));
        }

        /// <summary>
        /// Clean a multi-line string to make it more readable
        /// </summary>
        /// <param name="text">Multi-line string</param>
        /// <returns>Cleaned multi-line string</returns>
        public static string CleanMultilineString(string text)
        {
            text = (text ?? string.Empty).Trim();
            if (text.Length == 0)
            {
                return string.Empty;
            }

            string[] lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            StringBuilder sb = new StringBuilder();
            foreach (string line in lines)
            {
                string trimmedLine = line.Trim();
                if (trimmedLine.Length != 0)
                {
                    sb.Append(trimmedLine);
                    sb.Append('\n');
                }
            }
            return sb.ToString().Trim();
        }

        /// <summary>
        /// Get a value from configuration manager app settings
        /// </summary>
        /// <typeparam name="T">Type of value to get</typeparam>
        /// <param name="key">Key</param>
        /// <param name="defaultValue">Default value if null or not found</param>
        /// <returns>Value</returns>
        public T GetConfig<T>(string key, T defaultValue = default)
        {
            try
            {
                var value = appSettings[key];
                if (value != null)
                {
                    var converter = TypeDescriptor.GetConverter(typeof(T));
                    return (T)converter.ConvertFromInvariantString(value);
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Error deserializing appSettings key {0}", key);
            }
            return defaultValue;
        }

        /// <summary>
        /// Set a field / variable from configuration manager app settings. If null or not found, nothing is changed.
        /// </summary>
        /// <typeparam name="T">Type of value to set</typeparam>
        /// <param name="key">Key</param>
        /// <param name="value">Value</param>
        public void GetConfig<T>(string key, ref T value)
        {
            try
            {
                var converter = TypeDescriptor.GetConverter(typeof(T));
                if (appSettings.ContainsKey(key))
                {
                    value = (T)converter.ConvertFromInvariantString(appSettings[key]);
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Error deserializing appSettings key {0}", key);
            }
        }

        /// <summary>
        /// Get a value from configuration manager app settings
        /// </summary>
        /// <typeparam name="T">Type of value to get</typeparam>
        /// <param name="key">Key</param>
        /// <param name="value">Value to set</param>
        /// <param name="minValue">Min value</param>
        /// <param name="maxValue">Max value</param>
        /// <param name="clampSmallTimeSpan">Whether to clamp small timespan to max value</param>
        /// <returns>Value</returns>
        public void GetConfig<T>(string key, ref T value, T? minValue = null, T? maxValue = null, bool clampSmallTimeSpan = true) where T : struct, IComparable<T>
        {
            try
            {
                var converter = TypeDescriptor.GetConverter(typeof(T));
                value = (T)converter.ConvertFromInvariantString(appSettings[key]);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Error deserializing appSettings key {0}", key);
            }

            if (minValue != null && maxValue != null)
            {
                value = value.Clamp(minValue.Value, maxValue.Value, clampSmallTimeSpan);
            }
        }

        /// <summary>
        /// Set a field / variable from configuration manager app settings. If null or not found, nothing is changed.
        /// </summary>
        /// <typeparam name="T">Type of value to set</typeparam>
        /// <param name="key">Key</param>
        /// <param name="value">Value</param>
        /// <param name="defaultValue">Default value if array was empty</param>
        public void GetConfigArray<T>(string key, ref T[] value, T[] defaultValue)
        {
            try
            {
                var converter = TypeDescriptor.GetConverter(typeof(T));
                string[] items = (appSettings[key] ?? string.Empty).Split('|', ';', ',');
                List<T> list = new List<T>();
                foreach (string item in items)
                {
                    string normalizedItem = item.Trim();
                    if (normalizedItem.Length != 0)
                    {
                        list.Add((T)converter.ConvertFromInvariantString(normalizedItem));
                    }
                }
                if (list.Count == 0)
                {
                    value = (defaultValue ?? list.ToArray());
                }
                else
                {
                    value = list.ToArray();
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Error deserializing appSettings key {0}", key);
            }
        }

        /// <summary>
        /// Load IPBan config from XML
        /// </summary>
        /// <param name="xml">XML string</param>
        /// <param name="dns">Dns lookup for resolving ip addresses, null for default</param>
        /// <param name="dnsList">Dns server list, null for none</param>
        /// <returns>IPBanConfig</returns>
        public static IPBanConfig LoadFromXml(string xml, IDnsLookup dns = null, IDnsServerList dnsList = null)
        {
            return new IPBanConfig(xml, dns, dnsList);
        }

        /// <summary>
        /// Check if an entry is whitelisted
        /// </summary>
        /// <param name="entry">Entry</param>
        /// <returns>True if whitelisted, false otherwise</returns>
        public bool IsWhitelisted(string entry)
        {
            System.Net.IPAddress ipAddress = null;

            // if we have a dns list and the parameter is an ip address and the ip address
            // is one of our dns servers, it is whitelisted
            if (dnsList != null &&
                IPAddress.TryParse(entry, out ipAddress) &&
                dnsList.ContainsIPAddress(ipAddress))
            {
                return true;
            }
            return IsMatch(entry, ipAddress, whitelist, whitelistRanges, whitelistOther, whitelistRegex);
        }

        /// <summary>
        /// Check if an ip address range is whitelisted. If any whitelist ip or range intersects, the range is whitelisted.
        /// </summary>
        /// <param name="range">Range</param>
        /// <returns>True if range is whitelisted, false otherwise</returns>
        public bool IsWhitelisted(IPAddressRange range)
        {
            // if we have a dns list and one of our dns servers is in the range, the range is whitelisted
            if (dnsList != null && dnsList.ContainsIPAddressRange(range))
            {
                return true;
            }

            // if the whitelist ip address set contains the range or
            // the whitelist range set contains the range,
            // the passed in range is considered whitelisted
            else if (whitelist.Any(i => range.Contains(i)) ||
                whitelistRanges.Any(r => r.Contains(range)))
            {
                return true;
            }

            // it's possible the whitelist other list or whitelist regex will match, but it's too performance
            // intensive to scan every range in the incoming range to check, oh well...
            return false;
        }

        /// <summary>
        /// Check if an ip address, dns name or user name is blacklisted
        /// </summary>
        /// <param name="entry">IP address, dns name or user name</param>
        /// <returns>True if blacklisted, false otherwise</returns>
        public bool IsBlackListed(string entry)
        {
            System.Net.IPAddress ipAddress = null;

            // if we have a dns list and the parameter is an ip address and the ip address
            // is one of our dns servers, it is not blacklisted
            if (dnsList != null &&
                IPAddress.TryParse(entry, out ipAddress) &&
                dnsList.ContainsIPAddress(ipAddress))
            {
                return false;
            }

            return IsMatch(entry, ipAddress, blackList, blackListRanges, blackListOther, blackListRegex);
        }

        /// <summary>
        /// Check if a user name fails the user name whitelist regex. If the regex is empty, method returns false.
        /// </summary>
        /// <param name="userName">User name</param>
        /// <returns>True if failed the regex, false otherwise</returns>
        public bool UserNameFailsUserNameWhitelistRegex(string userName)
        {
            if (userNameWhitelistRegex is null)
            {
                return false;
            }
            userName = userName.Normalize().Trim();
            return !userNameWhitelistRegex.IsMatch(userName);
        }

        /// <summary>
        /// Checks if a user name is within the maximum edit distance for the user name whitelist.
        /// If the user name whitelist is empty, this method returns true.
        /// </summary>
        /// <param name="userName">User name</param>
        /// <returns>True if within max edit distance of any whitelisted user name, false otherwise.</returns>
        public bool IsUserNameWithinMaximumEditDistanceOfUserNameWhitelist(string userName)
        {
            if (userNameWhitelist.Count == 0)
            {
                return true;
            }

            userName = userName.Normalize().ToUpperInvariant().Trim();
            foreach (string userNameToCheckAgainst in userNameWhitelist)
            {
                int distance = LevenshteinUnsafe.Distance(userName, userNameToCheckAgainst);
                if (distance <= userNameWhitelistMaximumEditDistance)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Return all the groups that match the specified keywords (Windows only)
        /// </summary>
        /// <param name="keywords">Keywords</param>
        /// <returns>Groups that match</returns>
        public IEnumerable<EventViewerExpressionGroup> WindowsEventViewerGetGroupsMatchingKeywords(ulong keywords)
        {
            return WindowsEventViewerExpressionsToBlock.Groups.Where(g => (g.KeywordsULONG == keywords))
                .Union(expressionsSuccess.Groups.Where(g => (g.KeywordsULONG == keywords)));
        }

        /// <summary>
        /// Change an app settings - no XML encoding is done, so ensure your key and new value are already encoded
        /// </summary>
        /// <param name="config">Entire XML config</param>
        /// <param name="key">App setting key to look for</param>
        /// <returns>The config value or null if not found</returns>
        public static string GetConfigAppSetting(string config, string key)
        {
            string find = $@"\<add key=""{key}"" value=""(?<value>[^""]*)"" *\/\>";
            Match match = Regex.Match(config, find, RegexOptions.IgnoreCase);
            return (match is null || !match.Success ? null : match.Groups["value"].Value);
        }

        /// <summary>
        /// Change an app settings - no XML encoding is done, so ensure your key and new value are already encoded
        /// </summary>
        /// <param name="config">Entire XML config</param>
        /// <param name="key">App setting key to look for</param>
        /// <param name="newValue">Replacement value</param>
        /// <returns>Modified config or the config passed in if not found</returns>
        public static string ChangeConfigAppSetting(string config, string key, string newValue)
        {
            newValue ??= string.Empty;
            newValue = newValue.Replace("\"", "&quot;").Replace("'", "&apos;");
            string find = $@"\<add key=""{key}"" value=""[^""]*"" *\/\>";
            string replace = $@"<add key=""{key}"" value=""{newValue}"" />";
            return Regex.Replace(config, find, replace, RegexOptions.IgnoreCase);
        }

        /// <summary>
        /// Number of failed login attempts before a ban is initiated
        /// </summary>
        public int FailedLoginAttemptsBeforeBan { get { return failedLoginAttemptsBeforeBan; } }

        /// <summary>
        /// Length of time to ban an ip address, each unban and reban moves to the next TimeSpan in the array, until the last which then
        /// drops the ip address out of the ban list and starts over at the first TimeSpan in the array.
        /// This array will always have at least one value.
        /// </summary>
        public TimeSpan[] BanTimes { get { return banTimes; } }

        /// <summary>
        /// Whether to reset failed login count to 0 when an ip address is unbanned and using multiple BanTimes
        /// </summary>
        public bool ResetFailedLoginCountForUnbannedIPAddresses { get { return resetFailedLoginCountForUnbannedIPAddresses; } }

        /// <summary>
        /// The duration after the last failed login attempt that the count is reset back to 0.
        /// </summary>
        public TimeSpan ExpireTime { get { return expireTime; } }

        /// <summary>
        /// Interval of time to do house-keeping chores like un-banning ip addresses
        /// </summary>
        public TimeSpan CycleTime { get { return cycleTime; } }

        /// <summary>
        /// The minimum time between failed login attempts to increment the ban counter
        /// </summary>
        public TimeSpan MinimumTimeBetweenFailedLoginAttempts { get { return minimumTimeBetweenFailedLoginAttempts; } }

        /// <summary>
        /// The minimum time between successful login attempts to increment the success counter
        /// </summary>
        public TimeSpan MinimumTimeBetweenSuccessfulLoginAttempts { get { return minimumTimeBetweenSuccessfulLoginAttempts; } }

        /// <summary>
        /// Rule prefix for firewall
        /// </summary>
        public string FirewallRulePrefix { get { return firewallRulePrefix; } }

        /// <summary>
        /// Event viewer expressions to block (Windows only)
        /// </summary>
        public EventViewerExpressionsToBlock WindowsEventViewerExpressionsToBlock { get { return expressionsFailure; } }

        /// <summary>
        /// Event viewer expressions to notify for successful logins (Windows only)
        /// </summary>
        public EventViewerExpressionsToNotify WindowsEventViewerExpressionsToNotify { get { return expressionsSuccess; } }

        /// <summary>
        /// Log files to parse
        /// </summary>
        public IReadOnlyList<IPBanLogFileToParse> LogFilesToParse { get { return logFiles; } }

        /// <summary>
        /// True to clear and unban ip addresses upon restart, false otherwise
        /// </summary>
        public bool ClearBannedIPAddressesOnRestart { get { return clearBannedIPAddressesOnRestart; } }

        /// <summary>
        /// Whether to clear failed logins when an ip has a successful login (default false)
        /// </summary>
        public bool ClearFailedLoginsOnSuccessfulLogin { get { return clearFailedLoginsOnSuccessfulLogin; } }

        /// <summary>
        /// Get all ip address ranges in the blacklist
        /// </summary>
        public IReadOnlyCollection<IPAddressRange> BlackList
        {
            get { return blackList.Select(b => new IPAddressRange(b)).Union(blackListRanges).ToArray(); }
        }

        /// <summary>
        /// Black list regex
        /// </summary>
        public string BlackListRegex { get { return (blackListRegex is null ? string.Empty : blackListRegex.ToString()); } }

        /// <summary>
        /// Get all ip address ranges in the whitelist
        /// </summary>
        public IReadOnlyCollection<IPAddressRange> Whitelist
        {
            get { return whitelist.Select(b => new IPAddressRange(b)).Union(whitelistRanges).ToArray(); }
        }

        /// <summary>
        /// White list regex
        /// </summary>
        public string WhitelistRegex { get { return (whitelistRegex is null ? string.Empty : whitelistRegex.ToString()); } }

        /// <summary>
        /// White list user names. Any user name found not in the list is banned, unless the list is empty, in which case no checking is done.
        /// If not empty, Any user name within 'UserNameWhitelistMinimumEditDistance' in the config is also not banned.
        /// </summary>
        public IReadOnlyCollection<string> UserNameWhitelist { get { return userNameWhitelist; } }

        /// <summary>
        /// User name whitelist regex, or empty if not set.
        /// </summary>
        public string UserNameWhitelistRegex {  get { return (userNameWhitelistRegex is null ? string.Empty : userNameWhitelistRegex.ToString()); } }

        /// <summary>
        /// Number of failed logins before banning a user name in the user name whitelist
        /// </summary>
        public int FailedLoginAttemptsBeforeBanUserNameWhitelist { get { return failedLoginAttemptsBeforeBanUserNameWhitelist; } }

        /// <summary>
        /// Process to run on ban. See ReplaceUrl of IPBanService for place-holders.
        /// </summary>
        public string ProcessToRunOnBan { get { return processToRunOnBan; } }

        /// <summary>
        /// Whether to use the default banned ip address handler
        /// </summary>
        public bool UseDefaultBannedIPAddressHandler { get { return useDefaultBannedIPAddressHandler; } }

        /// <summary>
        /// List of extra block rules to create
        /// </summary>
        public IReadOnlyList<IPBanFirewallRule> ExtraRules { get { return extraRules; } }

        /// <summary>
        /// Config entry for firewall uri rules, one per line. Format is RulePrefix,Interval(DD:HH:MM:SS),Uri[NEWLINE]
        /// </summary>
        public string FirewallUriRules { get { return firewallUriRules; } }

        /// <summary>
        /// A url to get when the service updates, empty for none. See ReplaceUrl of IPBanService for place-holders.
        /// </summary>
        public string GetUrlUpdate { get { return getUrlUpdate; } }

        /// <summary>
        /// A url to get when the service starts, empty for none. See ReplaceUrl of IPBanService for place-holders.
        /// </summary>
        public string GetUrlStart { get { return getUrlStart; } }

        /// <summary>
        /// A url to get when the service stops, empty for none. See ReplaceUrl of IPBanService for place-holders.
        /// </summary>
        public string GetUrlStop { get { return getUrlStop; } }

        /// <summary>
        /// A url to get for a config file update, empty for none. See ReplaceUrl of IPBanService for place-holders.
        /// </summary>
        public string GetUrlConfig { get { return getUrlConfig; } }

        /// <summary>
        /// Url to query to get the external ip address, the url should return a string which is the external ip address.
        /// </summary>
        public string ExternalIPAddressUrl { get { return externalIPAddressUrl; } }
    }
}
