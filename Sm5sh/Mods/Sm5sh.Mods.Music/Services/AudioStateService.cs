﻿using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sm5sh.Data;
using Sm5sh.Data.Ui.Param.Database;
using Sm5sh.Data.Ui.Param.Database.PrcUiBgmDatabaseModels;
using Sm5sh.Data.Ui.Param.Database.PrcUiGameTitleDatabaseModels;
using Sm5sh.Helpers;
using Sm5sh.Interfaces;
using Sm5sh.Mods.Music.Data.Sound.Config;
using Sm5sh.Mods.Music.Data.Sound.Config.BgmPropertyStructs;
using Sm5sh.Mods.Music.Helpers;
using Sm5sh.Mods.Music.Interfaces;
using Sm5sh.Mods.Music.Models;
using Sm5sh.Mods.Music.Models.BgmEntryModels;
using Sm5sh.Mods.Music.Services.AudioStateServiceModels;
using Sm5sh.ResourceProviders.Prc.Helpers;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Sm5sh.Mods.Music.Services
{
    public class AudioStateService : IAudioStateService
    {
        private readonly ILogger _logger;
        private readonly IStateManager _state;
        private readonly IOptions<Sm5shMusicOptions> _config;
        private readonly Dictionary<string, BgmToneKeyReferences> _toneIdKeyReferences;

        public AudioStateService(IOptions<Sm5shMusicOptions> config, IStateManager state, ILogger<IAudioStateService> logger)
        {
            _config = config;
            _logger = logger;
            _state = state;
            _toneIdKeyReferences = new Dictionary<string, BgmToneKeyReferences>();
        }

        public IEnumerable<BgmEntry> GetBgmEntries()
        {
            return GetBgmEntriesFromStateManager().Values;
        }

        public IEnumerable<BgmEntry> GetModBgmEntries()
        {
            return GetBgmEntries().Where(p => !string.IsNullOrEmpty(p.FileName));
        }

        public BgmEntry GetBgmEntry(string toneId)
        {
            return GetBgmEntries().FirstOrDefault(p => p.ToneId == toneId);
        }

        public BgmEntry AddOrUpdateBgmEntry(BgmEntry bgmEntry)
        {
            var keyRefs = GetToneIdKeyReferences(bgmEntry.ToneId, bgmEntry.FileName);
            var bgmEntries = GetBgmEntriesFromStateManager();

            //Create
            if (!bgmEntries.ContainsKey(bgmEntry.ToneId))
                CreateNewBgmEntryInStateManager(keyRefs);

            //Update
            UpdateBgmEntryInStateManager(keyRefs, bgmEntry);

            //Return mapping BgmEntry
            return GetBgmEntry(bgmEntry.ToneId);
        }

        public void RemoveBgmEntry(string toneId)
        {
            RemoveBgmEntryFromStateManager(toneId);
        }

        #region Private
        private Dictionary<string, BgmEntry> GetBgmEntriesFromStateManager()
        {
            //Load BGM_PROPERTY
            var daoBinBgmProperty = _state.LoadResource<BinBgmProperty>(Constants.GameResources.PRC_BGM_PROPERTY_PATH);
            var daoBinPropertyEntries = daoBinBgmProperty.Entries;

            //Load UI_BGM_DB / UI_GAMETITLE_DB
            var paramBgmDatabase = _state.LoadResource<PrcUiBgmDatabase>(Constants.GameResources.PRC_UI_BGM_DB_PATH);
            var paramGameTitleDbRoot = _state.LoadResource<PrcUiGameTitleDatabase>(Constants.GameResources.PRC_UI_GAMETITLE_DB_PATH).DbRootEntries;
            var paramBgmDbRoot = paramBgmDatabase.DbRootEntries;
            var paramBgmStreamSet = paramBgmDatabase.StreamSetEntries;
            var paramBgmAssignedInfo = paramBgmDatabase.AssignedInfoEntries;
            var paramBgmStreamProperty = paramBgmDatabase.StreamPropertyEntries;
            var paramBgmPlaylists = paramBgmDatabase.PlaylistEntries.ToDictionary(p => p.Id.StringValue, p => p.Values);

            //Load MSBT
            var daoMsbtBgms = GetBgmDatabases();
            var daoMsbtTitle = GetGameTitleDatabases();

            var output = new Dictionary<string, BgmEntry>();
            foreach (var dbRootEntryKeyValue in paramBgmDbRoot)
            {
                var toneId = dbRootEntryKeyValue.Key.Replace(Constants.InternalIds.UI_BGM_ID_PREFIX, string.Empty);
                var keyRef = GetToneIdKeyReferences(toneId);
                
                //For now, we're only treating songs that have all the data we need
                if (!paramBgmStreamSet.ContainsKey(keyRef.StreamSetKey) || !paramBgmAssignedInfo.ContainsKey(keyRef.AssignedInfoKey) ||
                   !paramBgmStreamProperty.ContainsKey(keyRef.StreamPropertyKey) || !daoBinPropertyEntries.ContainsKey(toneId))
                    continue;

                var dbRootEntry = dbRootEntryKeyValue.Value;
                var setStreamEntry = paramBgmStreamSet[keyRef.StreamSetKey];
                var assignedInfoEntry = paramBgmAssignedInfo[keyRef.AssignedInfoKey];
                var streamPropertyEntry = paramBgmStreamProperty[keyRef.StreamPropertyKey];
                var gameTitleEntry = paramGameTitleDbRoot[dbRootEntry.UiGameTitleId.StringValue];
                var bgmProperty = daoBinPropertyEntries[toneId];

                var newBgmEntry = new BgmEntry()
                {
                    ToneId = toneId,
                    GameTitle = new GameTitleEntry()
                    {
                        GameTitleId = dbRootEntry.UiGameTitleId.StringValue,
                        NameId = dbRootEntry.NameId,
                        SeriesId = gameTitleEntry.UiSeriesId.StringValue,
                        Title = new Dictionary<string, string>()
                    },
                    RecordType = dbRootEntry.RecordType.StringValue,
                    AudioCuePoints = new AudioCuePoints()
                    {
                        LoopEndMs = bgmProperty.LoopEndMs,
                        LoopEndSample = bgmProperty.LoopEndSample,
                        LoopStartMs = bgmProperty.LoopStartMs,
                        LoopStartSample = bgmProperty.LoopStartSample,
                        TotalSamples = bgmProperty.TotalSamples,
                        TotalTimeMs = bgmProperty.TotalTimeMs
                    },
                    FileName = _toneIdKeyReferences.ContainsKey(toneId) ? _toneIdKeyReferences[toneId].FileName : null,
                    Playlists = paramBgmPlaylists.Where(p => p.Value.Any(p => p.UiBgmId.HexValue == dbRootEntry.UiBgmId.HexValue)).Select(p => new PlaylistEntry() { Id = p.Key }).ToList(),
                    IsDlc = dbRootEntry.IsDlc,
                    IsPatch = dbRootEntry.IsPatch,
                    Title = new Dictionary<string, string>(),
                    Author = new Dictionary<string, string>(),
                    Copyright = new Dictionary<string, string>()
                };

                var nameId = dbRootEntry.NameId;
                var gameTitleId = gameTitleEntry.NameId;
                var gameTitleLabel = string.Format(Constants.InternalIds.MSBT_GAME_TITLE, gameTitleId);
                var titleLabel = string.Format(Constants.InternalIds.MSBT_BGM_TITLE, nameId);
                var authorLabel = string.Format(Constants.InternalIds.MSBT_BGM_AUTHOR, nameId);
                var copyrightLabel = string.Format(Constants.InternalIds.MSBT_BGM_COPYRIGHT, nameId);
                foreach (var msbtDb in daoMsbtBgms)
                {
                    var entries = msbtDb.Value.Entries;
                    if (entries.ContainsKey(titleLabel))
                        newBgmEntry.Title.Add(msbtDb.Key, entries[titleLabel]);
                    if (entries.ContainsKey(authorLabel))
                        newBgmEntry.Author.Add(msbtDb.Key, entries[authorLabel]);
                    if (entries.ContainsKey(copyrightLabel))
                        newBgmEntry.Copyright.Add(msbtDb.Key, entries[copyrightLabel]);
                }
                foreach (var msbtDb in daoMsbtTitle)
                {
                    var entries = msbtDb.Value.Entries;
                    if (entries.ContainsKey(gameTitleLabel))
                        newBgmEntry.GameTitle.Title.Add(msbtDb.Key, entries[gameTitleLabel]);
                }

                output.Add(toneId, newBgmEntry);
            }

            return output;
        }

        private void CreateNewBgmEntryInStateManager(BgmToneKeyReferences keyRefs)
        {
            _logger.LogInformation("Adding Bgm Entry to State Service: {ToneId}", keyRefs.ToneId);

            var paramBgmDatabase = _state.LoadResource<PrcUiBgmDatabase>(Constants.GameResources.PRC_UI_BGM_DB_PATH);
            var paramBgmDbRoot = paramBgmDatabase.DbRootEntries;
            var daoBinBgmProperty = _state.LoadResource<BinBgmProperty>(Constants.GameResources.PRC_BGM_PROPERTY_PATH);

            //var saveNoIndex = (short)(_daoUiBgmDbRootEntries.Values.OrderByDescending(p => p.SaveNo).First().SaveNo + 1); //Not working past top save_no id
            var testDispOrderIndex = (short)(paramBgmDbRoot.Values.OrderByDescending(p => p.TestDispOrder).First().TestDispOrder + 1);
            var menuValueIndex = paramBgmDbRoot.Values.OrderByDescending(p => p.MenuValue).First().MenuValue + 1;

            //New entry - with default values
            paramBgmDbRoot.Add(keyRefs.DbRootKey, new PrcBgmDbRootEntry()
            {
                UiBgmId = new PrcHash40(keyRefs.DbRootKey),
                StreamSetId = new PrcHash40(keyRefs.StreamSetKey),
                Rarity = new PrcHash40(Constants.InternalIds.RARITY_DEFAULT),
                RecordType = new PrcHash40(Constants.InternalIds.RECORD_TYPE_DEFAULT),
                UiGameTitleId = new PrcHash40(Constants.InternalIds.GAME_TITLE_ID_DEFAULT),
                UiGameTitleId1 = new PrcHash40(Constants.InternalIds.GAME_TITLE_ID_DEFAULT),
                UiGameTitleId2 = new PrcHash40(Constants.InternalIds.GAME_TITLE_ID_DEFAULT),
                UiGameTitleId3 = new PrcHash40(Constants.InternalIds.GAME_TITLE_ID_DEFAULT),
                UiGameTitleId4 = new PrcHash40(Constants.InternalIds.GAME_TITLE_ID_DEFAULT),
                NameId = GetNewBgmId(),
                SaveNo = 0,
                TestDispOrder = testDispOrderIndex,
                MenuValue = menuValueIndex,
                JpRegion = true,
                OtherRegion = true,
                Possessed = true,
                PrizeLottery = false,
                ShopPrice = 0,
                CountTarget = true,
                MenuLoop = 1,
                IsSelectableStageMake = true,
                Unk1 = true,
                Unk2 = true,
                IsDlc = false,
                IsPatch = false
            });
            paramBgmDatabase.StreamSetEntries.Add(keyRefs.StreamSetKey, new PrcBgmStreamSetEntry()
            {
                StreamSetId = new PrcHash40(keyRefs.StreamSetKey),
                SpecialCategory = new PrcHash40(0),
                Info0 = new PrcHash40(keyRefs.AssignedInfoKey)
            });
            paramBgmDatabase.AssignedInfoEntries.Add(keyRefs.AssignedInfoKey, new PrcBgmAssignedInfoEntry()
            {
                InfoId = new PrcHash40(keyRefs.AssignedInfoKey),
                StreamId = new PrcHash40(keyRefs.StreamPropertyKey),
                Condition = new PrcHash40(Constants.InternalIds.SOUND_CONDITION),
                ConditionProcess = new PrcHash40(0x1b9fe75d3f),
                ChangeFadoutFrame = 55,
                MenuChangeFadeOutFrame = 55
            });
            paramBgmDatabase.StreamPropertyEntries.Add(keyRefs.StreamPropertyKey, new PrcBgmStreamPropertyEntry()
            {
                StreamId = new PrcHash40(keyRefs.StreamPropertyKey),
                DateName0 = keyRefs.ToneId,
                Loop = 1,
                EndPoint = "00:00:15.000",
                FadeOutFrame = 400,
                StartPointTransition = "00:00:04.000"
            });
            daoBinBgmProperty.Entries.Add(keyRefs.ToneId, new BgmPropertyEntry());
        }

        private void UpdateBgmEntryInStateManager(BgmToneKeyReferences keyRefs, BgmEntry bgmEntry)
        {
            _logger.LogInformation("Updating Bgm Entry to State Service: {ToneId}", keyRefs.ToneId);

            var paramBgmDatabase = _state.LoadResource<PrcUiBgmDatabase>(Constants.GameResources.PRC_UI_BGM_DB_PATH);
            var paramGameTitleDatabaseRoot = _state.LoadResource<PrcUiGameTitleDatabase>(Constants.GameResources.PRC_UI_GAMETITLE_DB_PATH).DbRootEntries;
            var binBgmPropertyEntries = _state.LoadResource<BinBgmProperty>(Constants.GameResources.PRC_BGM_PROPERTY_PATH).Entries;

            var defaultLocale = _config.Value.Sm5shMusic.DefaultLocale;
            var coreSeriesGames = paramGameTitleDatabaseRoot.Values.Select(p => p.UiSeriesId.StringValue).Distinct(); //Not handling series addition right now.

            var toneId = keyRefs.ToneId;

            if (!paramBgmDatabase.DbRootEntries.ContainsKey(keyRefs.DbRootKey))
                throw new Exception($"BGM ID {keyRefs.DbRootKey} does not exist in the DBRoot");

            //BGM PRC
            var dbRootEntry = paramBgmDatabase.DbRootEntries[keyRefs.DbRootKey];
            var setStreamEntry = paramBgmDatabase.StreamSetEntries[keyRefs.StreamSetKey];
            var assignedInfoEntry = paramBgmDatabase.AssignedInfoEntries[keyRefs.AssignedInfoKey];
            var streamPropertyEntry = paramBgmDatabase.StreamPropertyEntries[keyRefs.StreamPropertyKey];

            dbRootEntry.UiGameTitleId = new PrcHash40(bgmEntry.GameTitle.GameTitleId);
            dbRootEntry.RecordType = new PrcHash40(bgmEntry.RecordType);
            dbRootEntry.IsPatch = bgmEntry.IsPatch;
            dbRootEntry.IsDlc = bgmEntry.IsDlc;

            //GameTitle PRC
            if (!paramGameTitleDatabaseRoot.ContainsKey(bgmEntry.GameTitle.GameTitleId))
            {
                string seriesId = bgmEntry.GameTitle.SeriesId;
                if (!coreSeriesGames.Contains(seriesId))
                    seriesId = Constants.InternalIds.GAME_SERIES_ID_DEFAULT;
                paramGameTitleDatabaseRoot.Add(bgmEntry.GameTitle.GameTitleId, new PrcGameTitleDbRootEntry()
                {
                    NameId = bgmEntry.GameTitle.NameId,
                    Release = paramGameTitleDatabaseRoot.Values.OrderByDescending(p => p.Release).First().Release + 1,
                    UiGameTitleId = new PrcHash40(bgmEntry.GameTitle.GameTitleId),
                    UiSeriesId = new PrcHash40(seriesId)
                });
            }

            //Bin Property
            binBgmPropertyEntries[toneId].TotalSamples = bgmEntry.AudioCuePoints.TotalSamples;
            binBgmPropertyEntries[toneId].LoopEndMs = bgmEntry.AudioCuePoints.LoopEndMs;
            binBgmPropertyEntries[toneId].LoopEndSample = bgmEntry.AudioCuePoints.LoopEndSample;
            binBgmPropertyEntries[toneId].LoopStartMs = bgmEntry.AudioCuePoints.LoopStartMs;
            binBgmPropertyEntries[toneId].LoopStartSample = bgmEntry.AudioCuePoints.LoopStartSample;
            binBgmPropertyEntries[toneId].TotalTimeMs = bgmEntry.AudioCuePoints.TotalTimeMs;
            binBgmPropertyEntries[toneId].NameId = toneId;

            //Playlists
            foreach (var playlistId in bgmEntry.Playlists)
            {
                var paramBgmPlaylist = paramBgmDatabase.PlaylistEntries.FirstOrDefault(p => p.Id.StringValue == playlistId.Id)?.Values;
                if (paramBgmPlaylist == null)
                {
                    paramBgmPlaylist = new List<PrcBgmPlaylistEntry>();
                    paramBgmDatabase.PlaylistEntries.Add(new PcrFilterStruct<PrcBgmPlaylistEntry>()
                    {
                        Id = new PrcHash40(playlistId.Id),
                        Values = paramBgmPlaylist
                    });
                }

                var newPlaylistEntry = new PrcBgmPlaylistEntry() { UiBgmId = new PrcHash40(dbRootEntry.UiBgmId.StringValue) };
                newPlaylistEntry.SetOrder((short)paramBgmPlaylist.Count);
                newPlaylistEntry.SetIncidence(500);
                paramBgmPlaylist.Add(newPlaylistEntry);
            }

            //MSBT
            var nameId = dbRootEntry.NameId;
            var gameTitleEntry = paramGameTitleDatabaseRoot[dbRootEntry.UiGameTitleId.StringValue];
            var gameTitleId = gameTitleEntry.NameId;
            var gameTitleLabel = string.Format(Constants.InternalIds.MSBT_GAME_TITLE, gameTitleId);
            var titleLabel = string.Format(Constants.InternalIds.MSBT_BGM_TITLE, nameId);
            var authorLabel = string.Format(Constants.InternalIds.MSBT_BGM_AUTHOR, nameId);
            var copyrightLabel = string.Format(Constants.InternalIds.MSBT_BGM_COPYRIGHT, nameId);
            foreach (var msbtDb in GetBgmDatabases())
            {
                var entries = msbtDb.Value.Entries;

                if (bgmEntry.Title.ContainsKey(msbtDb.Key))
                    entries[titleLabel] = bgmEntry.Title[msbtDb.Key];
                else if (bgmEntry.Title.ContainsKey(defaultLocale))
                    entries[titleLabel] = bgmEntry.Title[defaultLocale];
                else
                    entries[titleLabel] = "MISSING";

                if (bgmEntry.Author.ContainsKey(msbtDb.Key))
                    entries[authorLabel] = bgmEntry.Author[msbtDb.Key];
                else if (bgmEntry.Author.ContainsKey(defaultLocale))
                    entries[authorLabel] = bgmEntry.Author[defaultLocale];

                if (bgmEntry.Copyright.ContainsKey(msbtDb.Key))
                    entries[copyrightLabel] = bgmEntry.Copyright[msbtDb.Key];
                else if (bgmEntry.Copyright.ContainsKey(defaultLocale))
                    entries[copyrightLabel] = bgmEntry.Copyright[defaultLocale];
            }
            foreach (var msbtDb in GetGameTitleDatabases())
            {
                var entries = msbtDb.Value.Entries;
                if (bgmEntry.GameTitle.Title.ContainsKey(msbtDb.Key))
                    entries[gameTitleLabel] = bgmEntry.GameTitle.Title[msbtDb.Key];
                else if (bgmEntry.GameTitle.Title.ContainsKey(defaultLocale))
                    entries[gameTitleLabel] = bgmEntry.GameTitle.Title[defaultLocale];
                else
                    entries[gameTitleLabel] = "MISSING";
            }
        }

        private void RemoveBgmEntryFromStateManager(string toneId)
        {
            var keyRefs = GetToneIdKeyReferences(toneId);

            var binBgmProperty = _state.LoadResource<BinBgmProperty>(Constants.GameResources.PRC_BGM_PROPERTY_PATH);
            var paramBgmDatabase = _state.LoadResource<PrcUiBgmDatabase>(Constants.GameResources.PRC_UI_BGM_DB_PATH);

            //If not in DBROOT, skip but no error
            if (!paramBgmDatabase.DbRootEntries.ContainsKey(keyRefs.DbRootKey))
                return;

            //BIN
            binBgmProperty.Entries.Remove(toneId);

            //PRC
            var dbRootRef = paramBgmDatabase.DbRootEntries[keyRefs.DbRootKey];
            paramBgmDatabase.DbRootEntries.Remove(keyRefs.DbRootKey);
            paramBgmDatabase.StreamSetEntries.Remove(keyRefs.StreamSetKey);
            paramBgmDatabase.AssignedInfoEntries.Remove(keyRefs.AssignedInfoKey);
            paramBgmDatabase.StreamPropertyEntries.Remove(keyRefs.StreamPropertyKey);

            //TODO GAMETITLE
            //TODO GAMETITLE
            //TODO GAMETITLE

            //PLAYLISTS
            foreach (var playlist in paramBgmDatabase.PlaylistEntries)
                playlist.Values.RemoveAll(p => p.UiBgmId.StringValue == keyRefs.DbRootKey);

            //MSBT
            if (dbRootRef != null)
            {
                var nameId = dbRootRef.NameId;
                var titleLabel = string.Format(Constants.InternalIds.MSBT_BGM_TITLE, nameId);
                var authorLabel = string.Format(Constants.InternalIds.MSBT_BGM_AUTHOR, nameId);
                var copyrightLabel = string.Format(Constants.InternalIds.MSBT_BGM_COPYRIGHT, nameId);
                foreach (var msbtBgm in GetBgmDatabases().Values)
                {
                    msbtBgm.Entries.Remove(titleLabel);
                    msbtBgm.Entries.Remove(authorLabel);
                    msbtBgm.Entries.Remove(copyrightLabel);
                }
            }
        }

        #region Utils
        private string GetNewBgmId()
        {
            var paramBgmDatabase = _state.LoadResource<PrcUiBgmDatabase>(Constants.GameResources.PRC_UI_BGM_DB_PATH);
            var lastNameId = paramBgmDatabase.DbRootEntries.Values.Where(p => p.NameId != "random" && !string.IsNullOrEmpty(p.NameId)).OrderByDescending(p => Base36IncrementHelper.ToInt(p.NameId)).FirstOrDefault()?.NameId;
            lastNameId = Base36IncrementHelper.ToString(Base36IncrementHelper.ToInt(lastNameId) + 1);
            if (lastNameId == "random")
                return GetNewBgmId();
            return lastNameId;
        }

        private BgmToneKeyReferences GetToneIdKeyReferences(string toneId, string filename = null)
        {
            if (_toneIdKeyReferences.ContainsKey(toneId))
                return _toneIdKeyReferences[toneId];
            var output = new BgmToneKeyReferences(toneId, filename);
            _toneIdKeyReferences.Add(toneId, output);
            return output;
        }

        private Dictionary<string, MsbtDatabase> GetBgmDatabases()
        {
            var output = new Dictionary<string, MsbtDatabase>();
            foreach (var locale in LocaleHelper.ValidLocales)
            {
                var msbt = _state.LoadResource<MsbtDatabase>(string.Format(Constants.GameResources.MSBT_BGM, locale), true);
                if (msbt != null)
                    output.Add(locale, msbt);
            }
            return output;
        }

        private Dictionary<string, MsbtDatabase> GetGameTitleDatabases()
        {
            var output = new Dictionary<string, MsbtDatabase>();
            foreach (var locale in LocaleHelper.ValidLocales)
            {
                var msbt = _state.LoadResource<MsbtDatabase>(string.Format(Constants.GameResources.MSBT_TITLE, locale), true);
                if (msbt != null)
                    output.Add(locale, msbt);
            }
            return output;
        }
        #endregion
        #endregion
    }

    namespace AudioStateServiceModels
    {
        public enum BgmDbOperation
        {
            Added,
            Removed
        }

        public class BgmToneKeyReferences
        {
            public string ToneId { get; }
            public string FileName { get; }
            public string DbRootKey { get; }
            public string StreamSetKey { get; }
            public string AssignedInfoKey { get; }
            public string StreamPropertyKey { get; }

            public BgmToneKeyReferences(string toneId, string fileName = null)
            {
                ToneId = toneId;
                DbRootKey = $"{Constants.InternalIds.UI_BGM_ID_PREFIX}{toneId}";
                StreamSetKey = $"{Constants.InternalIds.STREAM_SET_PREFIX}{toneId}";
                AssignedInfoKey = $"{Constants.InternalIds.INFO_ID_PREFIX}{toneId}";
                StreamPropertyKey = $"{Constants.InternalIds.STREAM_PREFIX}{toneId}";
                FileName = fileName;
            }
        }
    }
}
