using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using TESL.Accelerator.Core.Abstractions;
using TESL.Accelerator.Core.Models;

namespace TESL.Accelerator.Core.Services
{
    public class CandidatePool
    {
        private readonly List<string> _candidates443 = new();
        private readonly List<string> _candidates8443 = new();
        private readonly HashSet<string> _seen443 = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _seen8443 = new(StringComparer.OrdinalIgnoreCase);
        private readonly object _lock = new();

        public IReadOnlyList<string> Candidates443 { get { lock (_lock) { return _candidates443.ToList(); } } }
        public IReadOnlyList<string> Candidates8443 { get { lock (_lock) { return _candidates8443.ToList(); } } }

        public void Clear()
        {
            lock (_lock)
            {
                _candidates443.Clear();
                _candidates8443.Clear();
                _seen443.Clear();
                _seen8443.Clear();
            }
        }

        public int AddCandidates443(IEnumerable<string> ips, int maxLimit = 128)
        {
            lock (_lock)
            {
                int count = 0;
                foreach (var ip in ips)
                {
                    if (_candidates443.Count >= maxLimit) break;
                    if (_seen443.Add(ip))
                    {
                        _candidates443.Add(ip);
                        count++;
                    }
                }
                return count;
            }
        }

        public int AddCandidates8443(IEnumerable<string> ips, int maxLimit = 128)
        {
            lock (_lock)
            {
                int count = 0;
                foreach (var ip in ips)
                {
                    if (_candidates8443.Count >= maxLimit) break;
                    if (_seen8443.Add(ip))
                    {
                        _candidates8443.Add(ip);
                        count++;
                    }
                }
                return count;
            }
        }

        public bool SaveToFiles(string path443, string path8443, int minRequired = 10)
        {
            lock (_lock)
            {
                bool ok443 = false;
                bool ok8443 = false;

                if (_candidates443.Count >= minRequired)
                {
                    CacheManager.AtomicWriteText(path443, string.Join("\n", _candidates443) + "\n");
                    ok443 = true;
                }

                if (_candidates8443.Count >= minRequired)
                {
                    CacheManager.AtomicWriteText(path8443, string.Join("\n", _candidates8443) + "\n");
                    ok8443 = true;
                }

                return ok443 && ok8443;
            }
        }

        public bool LoadExistingCandidates(string path443, string path8443, int minRequired = 10)
        {
            lock (_lock)
            {
                if (File.Exists(path443))
                {
                    var lines = File.ReadAllLines(path443)
                        .Select(l => l.Trim())
                        .Where(l => !string.IsNullOrWhiteSpace(l) && !l.StartsWith("#") && !l.StartsWith("//"));
                    AddCandidates443(lines);
                }

                if (File.Exists(path8443))
                {
                    var lines = File.ReadAllLines(path8443)
                        .Select(l => l.Trim())
                        .Where(l => !string.IsNullOrWhiteSpace(l) && !l.StartsWith("#") && !l.StartsWith("//"));
                    AddCandidates8443(lines);
                }

                return _candidates443.Count >= minRequired && _candidates8443.Count >= minRequired;
            }
        }
    }
}
