using System.Collections.Generic;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using UnityEngine;

// Built based on original code from Dixie on the Sailwind Discord.
// Developed with assistance from GPT-5.6 Luna.

namespace SoundEffectVolumeLimiter
{
    [BepInPlugin("com.Exocet.SoundEffectVolumeLimiter", "Sound Effect Volume Limiter", "1.0.0")]

    public class SoundEffectVolumeLimiterPlugin : BaseUnityPlugin
    {
        private void Awake()
        {
            ShipSoundCaps.Bind(Config);
            gameObject.AddComponent<ShipSoundCaps>();
        }
    }

    // ConfigurationManager reads this metadata through ConfigDescription.Tags without requiring
    // the plugin to reference ConfigurationManager.dll directly.
    internal sealed class ConfigurationManagerAttributes
    {
        public int? Order;
    }

    /// <summary>
    /// Caps matching audio sources across the entire active Unity scene.
    /// Edit the default groups or config entries to target specific effects.
    /// </summary>
    public class ShipSoundCaps : MonoBehaviour
    {
        // Group names are defined in code as defaults, while users can override them from the
        // config menu at runtime. Keywords remain separate so names can change independently.
        private static readonly string[] DefaultGroupNames =
        {
            "Footsteps",
            "Helm Creak",
            "Boat Creaking",
            "Hull Wash",
            "Waves",
            "Rigging",
            "Weather",
            "Misc Effects",
            "Custom 1",
            "Custom 2"
        };

        private static readonly string[][] DefaultGroupKeywords =
        {
            new[] { "footstep", "footsteps", "foot", "step", "steps" },
            new[] { "wheel creak" },
            new[] { "boat creak"},
            new[] { "hull", "hitting-bow", "bow" },
            new[] { "oceanwave", "wave", "splash", "ocean", "shore" },
            new[] { "sail", "rope", "flap", "snap" },
            new[] { "rain", "thunder", "storm", "wind" },
            new[] { "click", "eat", "swallow", "scrape", "fishing", "seagulls" },
            new[] { "bell" },
            new[] { "pump" }
        };

        private sealed class SoundGroup
        {
            public string Name;
            public string[] Keywords;
        }

        private static readonly SoundGroup[] DefaultGroups = CreateDefaultGroups();
        private static SoundGroup[] _groups = CreateDefaultGroups();
        private static ConfigEntry<string>[] _groupNames;
        private static ConfigEntry<string>[] _groupKeywords;
        private static ConfigEntry<float>[] _caps;
        private static ConfigEntry<KeyboardShortcut> _dumpKey;
        private static ConfigEntry<KeyboardShortcut> _refreshGroupsKey;
        private static ConfigEntry<KeyboardShortcut> _restoreDefaultGroupsKey;
        private static readonly HashSet<ShipSoundCaps> Instances = new HashSet<ShipSoundCaps>();
        private static readonly List<AudioSource> SceneAudioSources = new List<AudioSource>();

        private static SoundGroup[] CreateDefaultGroups()
        {
            SoundGroup[] groups = new SoundGroup[DefaultGroupNames.Length];
            for (int i = 0; i < DefaultGroupNames.Length; i++)
            {
                groups[i] = new SoundGroup
                {
                    Name = DefaultGroupNames[i],
                    Keywords = CopyKeywords(DefaultGroupKeywords[i])
                };
            }

            return groups;
        }

        private static string[] CopyKeywords(string[] keywords)
        {
            string[] copied = new string[keywords.Length];
            for (int i = 0; i < keywords.Length; i++)
                copied[i] = keywords[i].ToLowerInvariant();
            return copied;
        }

        public static void Bind(ConfigFile cfg)
        {
            _groupNames = new ConfigEntry<string>[DefaultGroupNames.Length];
            _groupKeywords = new ConfigEntry<string>[DefaultGroupNames.Length];

            for (int i = 0; i < DefaultGroupNames.Length; i++)
            {
                _groupNames[i] = cfg.Bind(
                    "Ship sound",
                    "Group " + (i + 1) + " name",
                    DefaultGroupNames[i],
                    new ConfigDescription(
                        "Display name for this ship sound group.",
                        null,
                        new ConfigurationManagerAttributes { Order = GroupOrder(i, 0) }));

                _groupKeywords[i] = cfg.Bind(
                    "Ship sound",
                    "Group " + (i + 1) + " keywords",
                    string.Join(";", DefaultGroupKeywords[i]),
                    new ConfigDescription(
                        "Semicolon-separated clip fragments used to detect sounds for this group. Example: wave; splash",
                        null,
                        new ConfigurationManagerAttributes { Order = GroupOrder(i, 1) }));

                _groupNames[i].SettingChanged += (_, __) => RefreshGroupsFromConfig();
                _groupKeywords[i].SettingChanged += (_, __) => RefreshGroupsFromConfig();
            }

            RefreshGroupsFromConfig();

            _caps = new ConfigEntry<float>[_groups.Length];
            for (int i = 0; i < _groups.Length; i++)
            {
                _caps[i] = cfg.Bind(
                    "Ship sound",
                    _groups[i].Name + " volume ceiling",
                    1f,
                    new ConfigDescription(
                        "1 = untouched, 0 = silent. This is a ceiling: anything already quieter than this is left alone.",
                        new AcceptableValueRange<float>(0f, 1f),
                        new ConfigurationManagerAttributes { Order = GroupOrder(i, 2) }));
            }

            _dumpKey = cfg.Bind(
                "Ship sound",
                "List the clips on this ship",
                new KeyboardShortcut(KeyCode.F11),
                new ConfigDescription(
                    "Press this key in-game to list matching and ungrouped audio clips.",
                    null,
                    new ConfigurationManagerAttributes { Order = 520 }));

            _refreshGroupsKey = cfg.Bind(
                "Ship sound",
                "Refresh groups key",
                new KeyboardShortcut(KeyCode.F9),
                new ConfigDescription(
                    "Press this key in-game to rebuild the configured groups and rescan all active audio sources.",
                    null,
                    new ConfigurationManagerAttributes { Order = 540 }));

            _restoreDefaultGroupsKey = cfg.Bind(
                "Ship sound",
                "Restore default groups key",
                new KeyboardShortcut(KeyCode.F8),
                new ConfigDescription(
                    "Press this key in-game to restore the default group names and keyword sets without changing volume ceilings.",
                    null,
                    new ConfigurationManagerAttributes { Order = 560 }));
        }

        private static int GroupOrder(int groupIndex, int settingOffset)
        {
            return 1000 - groupIndex * 30 - settingOffset;
        }

        private static void RefreshGroupsFromConfig()
        {
            if (_groupNames == null || _groupKeywords == null)
                return;

            _groups = new SoundGroup[_groupNames.Length];
            for (int i = 0; i < _groupNames.Length; i++)
            {
                string name = _groupNames[i] != null && !string.IsNullOrWhiteSpace(_groupNames[i].Value)
                    ? _groupNames[i].Value.Trim()
                    : DefaultGroups[i].Name;

                List<string> keywords = new List<string>();
                keywords.Add(name.ToLowerInvariant());

                if (_groupKeywords[i] != null && !string.IsNullOrEmpty(_groupKeywords[i].Value))
                {
                    string[] pieces = _groupKeywords[i].Value.Split(new[] { ';', ',', '|' }, System.StringSplitOptions.RemoveEmptyEntries);
                    for (int j = 0; j < pieces.Length; j++)
                    {
                        string fragment = pieces[j].Trim();
                        if (!string.IsNullOrEmpty(fragment))
                            keywords.Add(fragment.ToLowerInvariant());
                    }
                }

                if (keywords.Count == 1)
                {
                    for (int j = 0; j < DefaultGroups[i].Keywords.Length; j++)
                        keywords.Add(DefaultGroups[i].Keywords[j]);
                }

                _groups[i] = new SoundGroup
                {
                    Name = name,
                    Keywords = keywords.ToArray()
                };
            }
        }

        private readonly List<AudioSource>[] _byGroup = new List<AudioSource>[DefaultGroups.Length];
        private readonly HashSet<AudioSource> _mutedByUs = new HashSet<AudioSource>();
        private float _nextScan;

        private void Awake()
        {
            Instances.Add(this);
            for (int i = 0; i < _groups.Length; i++)
                _byGroup[i] = new List<AudioSource>();

            Rescan();
        }

        private void OnDestroy()
        {
            Instances.Remove(this);
        }

        private static List<AudioSource> FindSceneAudioSources()
        {
            AudioSource[] all = Resources.FindObjectsOfTypeAll<AudioSource>();
            SceneAudioSources.Clear();

            for (int i = 0; i < all.Length; i++)
            {
                AudioSource source = all[i];
                if (source != null && source.gameObject.scene.IsValid())
                    SceneAudioSources.Add(source);
            }

            return SceneAudioSources;
        }

        private void Rescan()
        {
            _mutedByUs.RemoveWhere(IsDestroyedAudioSource);
            for (int i = 0; i < _byGroup.Length; i++)
                _byGroup[i].Clear();

            List<AudioSource> all = FindSceneAudioSources();
            for (int i = 0; i < all.Count; i++)
            {
                AudioSource a = all[i];
                if (a == null || a.clip == null || a.clip.name == null)
                    continue;

                string clip = a.clip.name.ToLowerInvariant();
                int groupIndex = FindMatchingGroup(clip);
                if (groupIndex >= 0)
                {
                    _byGroup[groupIndex].Add(a);
                }
            }
        }

        private static int FindMatchingGroup(string clipName)
        {
            // Match groups strictly in the DefaultGroupNames order so each clip is assigned
            // to the earliest configured group that contains a matching keyword.
            for (int groupIndex = 0; groupIndex < _groups.Length; groupIndex++)
            {
                SoundGroup group = _groups[groupIndex];
                for (int keywordIndex = 0; keywordIndex < group.Keywords.Length; keywordIndex++)
                {
                    string keyword = group.Keywords[keywordIndex];
                    if (!string.IsNullOrEmpty(keyword) && clipName.Contains(keyword))
                        return groupIndex;
                }
            }

            return -1;
        }

        private void LateUpdate()
        {
            if (_caps == null)
                return;

            if (Time.time >= _nextScan)
            {
                _nextScan = Time.time + 5f;
                Rescan();
            }

            if (_dumpKey != null && _dumpKey.Value.IsDown())
                Dump();

            if (_refreshGroupsKey != null && _refreshGroupsKey.Value.IsDown())
                Rescan();

            if (_restoreDefaultGroupsKey != null && _restoreDefaultGroupsKey.Value.IsDown())
                RestoreDefaultGroups();

            for (int g = 0; g < _byGroup.Length; g++)
            {
                float cap = _caps[g].Value;
                List<AudioSource> list = _byGroup[g];

                for (int i = 0; i < list.Count; i++)
                {
                    AudioSource a = list[i];
                    if (a == null)
                        continue;

                    if (cap <= 0.001f)
                    {
                        if (!a.mute)
                        {
                            a.mute = true;
                            _mutedByUs.Add(a);
                        }
                        continue;
                    }

                    if (_mutedByUs.Contains(a))
                    {
                        a.mute = false;
                        _mutedByUs.Remove(a);
                    }

                    if (cap >= 0.999f)
                        continue;

                    // Important: cap, never assign a hard value.
                    a.volume = Mathf.Min(a.volume, cap);
                }
            }
        }

        private static bool IsDestroyedAudioSource(AudioSource source)
        {
            return source == null;
        }

        private static void RestoreDefaultGroups()
        {
            if (_groupNames == null || _groupKeywords == null)
                return;

            for (int i = 0; i < DefaultGroupNames.Length; i++)
            {
                _groupNames[i].Value = DefaultGroupNames[i];
                _groupKeywords[i].Value = string.Join(";", DefaultGroupKeywords[i]);
            }

            RefreshGroupsFromConfig();
            RescanAllInstances();
            Debug.Log("[ShipSound] Restored default group order and keyword sets.");
        }

        private static void RescanAllInstances()
        {
            foreach (ShipSoundCaps instance in Instances)
            {
                if (instance != null)
                    instance.Rescan();
            }
        }

        private void Dump()
        {
            Rescan();
            for (int g = 0; g < _byGroup.Length; g++)
            {
                StringBuilder names = new StringBuilder();
                for (int i = 0; i < _byGroup[g].Count; i++)
                    if (_byGroup[g][i] != null && _byGroup[g][i].clip != null)
                        names.Append(_byGroup[g][i].clip.name).Append("  ");

                Debug.Log("[ShipSound] " + _groups[g].Name + " (" + _byGroup[g].Count + "): " + names);
            }

            List<AudioSource> all = FindSceneAudioSources();
            StringBuilder spare = new StringBuilder();
            for (int i = 0; i < all.Count; i++)
            {
                if (all[i] == null || all[i].clip == null)
                    continue;

                bool claimed = false;
                for (int g = 0; g < _byGroup.Length && !claimed; g++)
                    claimed = _byGroup[g].Contains(all[i]);

                if (!claimed)
                    spare.Append(all[i].clip.name).Append("  ");
            }

            Debug.Log("[ShipSound] UNGROUPED: " + spare);
        }
    }
}