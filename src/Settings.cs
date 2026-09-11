using System;
using System.IO;
using System.Web.Script.Serialization;

namespace IPCountryWatcher
{
    public sealed class Settings
    {
        public int PollSeconds { get; set; }
        public bool UseSystemProxy { get; set; }
        public bool NotifyOnChange { get; set; }
        public Settings() { PollSeconds = 5; UseSystemProxy = true; NotifyOnChange = true; }
        internal static string Folder { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IPCountryWatcher"); } }
        internal static Settings Load()
        {
            try
            {
                string file = Path.Combine(Folder, "settings.json");
                if (!File.Exists(file)) return new Settings();
                var settings = new JavaScriptSerializer().Deserialize<Settings>(File.ReadAllText(file));
                if (settings == null) return new Settings();
                if (settings.PollSeconds != 5 && settings.PollSeconds != 10 && settings.PollSeconds != 30 && settings.PollSeconds != 60)
                    settings.PollSeconds = 5;
                return settings;
            }
            catch (Exception ex)
            {
                if (!(ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is InvalidOperationException)) throw;
                return new Settings();
            }
        }
        internal void Save()
        {
            Directory.CreateDirectory(Folder);
            string file = Path.Combine(Folder, "settings.json");
            string temp = file + ".tmp";
            File.WriteAllText(temp, new JavaScriptSerializer().Serialize(this));
            if (File.Exists(file)) File.Replace(temp, file, null);
            else File.Move(temp, file);
        }
    }
}
