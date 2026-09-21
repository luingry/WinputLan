using System;
using System.IO;
using System.Runtime.Serialization.Json;
using System.Text;
using WinputLan.Core;

namespace WinputLan.Runtime
{
    public sealed class AppConfigStore
    {
        private readonly string _directory;
        private readonly string _path;

        public AppConfigStore(string directory)
        {
            _directory = directory ?? throw new ArgumentNullException("directory");
            _path = System.IO.Path.Combine(directory, "config.json");
        }

        public string Path { get { return _path; } }

        public WinputConfig LoadOrCreate()
        {
            try
            {
                if (File.Exists(_path))
                {
                    var config = Deserialize(File.ReadAllText(_path, Encoding.UTF8));
                    if (ConfigValidator.Validate(config).Count == 0) return config;
                }
            }
            catch { /* corrupt config fails safe to defaults */ }
            var fallback = WinputConfig.CreateDefault();
            Save(fallback);
            return fallback;
        }

        public void Save(WinputConfig config)
        {
            if (ConfigValidator.Validate(config).Count != 0) throw new InvalidDataException("Configuration is invalid.");
            Directory.CreateDirectory(_directory);
            var tempPath = _path + ".tmp";
            File.WriteAllText(tempPath, Serialize(config), new UTF8Encoding(false));
            if (File.Exists(_path)) File.Replace(tempPath, _path, null); else File.Move(tempPath, _path);
        }

        private static string Serialize(WinputConfig config)
        {
            var serializer = new DataContractJsonSerializer(typeof(WinputConfig));
            using (var stream = new MemoryStream()) { serializer.WriteObject(stream, config); return Encoding.UTF8.GetString(stream.ToArray()); }
        }

        private static WinputConfig Deserialize(string json)
        {
            if (string.IsNullOrWhiteSpace(json) || json.Length > 16384) throw new InvalidDataException("Configuration JSON is invalid.");
            var serializer = new DataContractJsonSerializer(typeof(WinputConfig));
            using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(json))) return (WinputConfig)serializer.ReadObject(stream);
        }
    }
}
