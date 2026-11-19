// Hallow Remote - the Game type shared with Hallow Hub's library scanner.
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;

namespace HallowRemote
{
    // A game found on this PC (same shape as Hallow Hub's, so the scanner is shared).
    [DataContract]
    public class Game
    {
        [DataMember] public string Id;
        [DataMember] public string Name;
        [DataMember] public string Source;
        [DataMember] public string InstallDir;
        [DataMember] public string Launch;
        [DataMember] public string Args = "";
        public string Cover, Hero, Logo, Header, IconExe; public string OriginalName;
        public DateTime UpdatedAt;
        public long SizeBytes; public string Drive; public string Uninstall;
        public bool NeedsUpdate, Updating; public double Progress = -1;
        public double SteamMinutes, Steam2Weeks; public DateTime? SteamLast;
        public List<Game> Alts = new List<Game>();
        public Game Parent;
        public string SourceName { get { return Source == "steam" ? "Steam" : Source == "battlenet" ? "Battle.net" : Source == "epic" ? "Epic Games" : Source == "ea" ? "EA app" : Source == "ubisoft" ? "Ubisoft Connect" : Source == "gog" ? "GOG" : Source == "riot" ? "Riot" : Source == "xbox" ? "Xbox" : Source == "hallow" ? "Hallow apps" : "Added by you"; } }
    }

}
