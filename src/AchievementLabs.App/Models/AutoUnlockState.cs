using System.IO;
using Newtonsoft.Json;

namespace AchievementLabs.Models
{
    public class AutoUnlockQueueEntry
    {
        /// <summary>
        /// Achievement ID to unlock
        /// </summary>
        public string AchievementId { get; set; } = "";

        /// <summary>
        /// Achievement name for display
        /// </summary>
        public string AchievementName { get; set; } = "";

        /// <summary>
        /// Gamerscore value of the achievement
        /// </summary>
        public int Gamerscore { get; set; }

        /// <summary>
        /// Delay in seconds before unlocking this achievement (time gap from previous unlock)
        /// </summary>
        public double DelaySeconds { get; set; }

        /// <summary>
        /// Whether this achievement has already been unlocked by the auto unlocker
        /// </summary>
        public bool Completed { get; set; }
        public int EventRequestIndex { get; set; }
    }

    public class AutoUnlockState
    {
        public string OwnerXuid { get; set; } = "";
        /// <summary>
        /// Reference gamertag used to build the queue
        /// </summary>
        public string ReferenceGamertag { get; set; } = "";

        /// <summary>
        /// Reference user's XUID
        /// </summary>
        public string ReferenceXuid { get; set; } = "";

        /// <summary>
        /// Title ID of the game being auto-unlocked
        /// </summary>
        public string TitleId { get; set; } = "";

        /// <summary>
        /// Game name for display
        /// </summary>
        public string GameName { get; set; } = "";

        /// <summary>
        /// Service config ID needed for title-based unlock calls
        /// </summary>
        public string ServiceConfigId { get; set; } = "";

        /// <summary>
        /// The ordered queue of achievements to unlock
        /// </summary>
        public List<AutoUnlockQueueEntry> Queue { get; set; } = new List<AutoUnlockQueueEntry>();

        /// <summary>
        /// Index of the next achievement to unlock in the queue
        /// </summary>
        public int CurrentIndex { get; set; }

        /// <summary>
        /// Remaining delay in seconds for the current item (for resume after restart)
        /// </summary>
        public double RemainingDelaySeconds { get; set; }

        /// <summary>
        /// Whether the auto unlock process is actively running
        /// </summary>
        public bool IsRunning { get; set; }
        public bool RefreshTokenOnFailure { get; set; }
        public bool NotifyOnFailure { get; set; } = true;
        public bool StopOnFailure { get; set; }

        /// <summary>
        /// Timestamp when the state was last saved
        /// </summary>
        public DateTime LastSaved { get; set; }

        /// <summary>
        /// Speed multiplier for time gaps (1.0 = real time, 0.5 = half speed, 2.0 = double speed)
        /// </summary>
        public double SpeedMultiplier { get; set; } = 1.0;

        /// <summary>
        /// Whether to use fake signature for unlock requests
        /// </summary>
        public bool UseFakeSignature { get; set; }

        /// <summary>
        /// Whether this game uses event-based unlocking (telemetry) instead of title-based
        /// </summary>
        public bool IsEventBased { get; set; }

        public static string GetSavePath()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "AchievementLabs", "auto_unlock_state.json");
        }

        public void Save()
        {
            LastSaved = DateTime.UtcNow;
            var dir = Path.GetDirectoryName(GetSavePath());
            if (!Directory.Exists(dir))
                Directory.CreateDirectory(dir!);
            File.WriteAllText(GetSavePath(), JsonConvert.SerializeObject(this, Formatting.Indented));
        }

        public static AutoUnlockState? Load()
        {
            var path = GetSavePath();
            if (!File.Exists(path))
                return null;
            try
            {
                var json = File.ReadAllText(path);
                return JsonConvert.DeserializeObject<AutoUnlockState>(json);
            }
            catch
            {
                return null;
            }
        }

        public static void Delete()
        {
            var path = GetSavePath();
            if (File.Exists(path))
                File.Delete(path);
        }
    }
}
