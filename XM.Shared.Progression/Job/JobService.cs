﻿using System;
using System.Collections.Generic;
using System.Linq;
using Anvil.Services;
using XM.Inventory;
using XM.Progression.Event;
using XM.Progression.Job.Entity;
using XM.Progression.Job.JobDefinition;
using XM.Shared.API.Constants;
using XM.Shared.API.NWNX.CreaturePlugin;
using XM.Shared.Core;
using XM.Shared.Core.Data;
using XM.Shared.Core.EventManagement;
using XM.Shared.Core.Localization;
using XM.UI.Event;

namespace XM.Progression.Job
{
    [ServiceBinding(typeof(JobService))]
    public class JobService
    {
        public const int LevelCap = 50;
        public const int ResonanceNodeLevelAcquisitionRate = 5; // One every 5 levels

        private readonly IList<IJobDefinition> _jobDefinitionsList;
        private readonly Dictionary<JobType, IJobDefinition> _jobDefinitions = new();

        private readonly Dictionary<JobType, ClassType> _jobsToClasses = new();
        private readonly Dictionary<ClassType, JobType> _classesToJobs = new();

        private readonly InventoryService _inventory;
        private readonly DBService _db;
        private readonly XMEventService _event;
        private readonly XPChart _xp;

        public JobService(
            InventoryService inventory, 
            DBService db,
            XMEventService @event,
            XPChart xp,
            IList<IJobDefinition> jobDefinitionsList)
        {
            _inventory = inventory;
            _db = db;
            _event = @event;
            _xp = xp;
            _jobDefinitionsList = jobDefinitionsList;

            CacheData();
            RegisterEvents();
            SubscribeEvents();
        }

        private void CacheData()
        {
            foreach (var definition in _jobDefinitionsList)
            {
                _jobDefinitions[definition.Type] = definition;
                _jobsToClasses[definition.Type] = definition.NWNClass;
                _classesToJobs[definition.NWNClass] = definition.Type;
            }
        }

        private void RegisterEvents()
        {
            _event.RegisterEvent<JobEvent.PlayerChangedJobEvent>(ProgressionEventScript.PlayerChangedJobScript);
            _event.RegisterEvent<JobEvent.PlayerLeveledUpEvent>(ProgressionEventScript.PlayerLeveledUpScript);
            _event.RegisterEvent<JobEvent.JobFeatAddedEvent>(ProgressionEventScript.JobFeatAddScript);
            _event.RegisterEvent<JobEvent.JobFeatRemovedEvent>(ProgressionEventScript.JobFeatRemovedScript);
        }

        private void SubscribeEvents()
        {
            _event.Subscribe<XMEvent.OnPCInitialized>(InitializeJob);
        }


        private void InitializeJob(uint player)
        {
            var @class = GetClassByPosition(1, player);
            var job = _classesToJobs[@class];

            ChangeJob(player, job, []);
        }

        internal Dictionary<JobType, IJobDefinition> GetAllJobDefinitions()
        {
            return _jobDefinitions.ToDictionary(x => x.Key, y => y.Value);
        }

        internal IJobDefinition GetJobDefinition(JobType job)
        {
            return _jobDefinitions[job];
        }

        public IJobDefinition GetActiveJob(uint player)
        {
            if (!GetIsPC(player) || GetIsDM(player) || GetIsDMPossessed(player))
                throw new ArgumentException("Only PCs can have jobs.");

            var playerId = PlayerId.Get(player);
            var dbPlayerJob = _db.Get<PlayerJob>(playerId) ?? new PlayerJob(playerId);
            return _jobDefinitions[dbPlayerJob.ActiveJob];
        }

        public int GetJobLevel(uint player, JobType job)
        {
            if (!GetIsPC(player) || GetIsDM(player) || GetIsDMPossessed(player))
                throw new ArgumentException("Only PCs can have jobs.");

            var playerId = PlayerId.Get(player);
            var dbPlayerJob = _db.Get<PlayerJob>(playerId) ?? new PlayerJob(playerId);
            var level = dbPlayerJob.JobLevels.ContainsKey(job)
                ? dbPlayerJob.JobLevels[job]
                : 0;
            return level;
        }

        public Dictionary<JobType, int> GetJobLevels(uint player)
        {
            if (!GetIsPC(player) || GetIsDM(player) || GetIsDMPossessed(player))
                throw new ArgumentException("Only PCs can have jobs.");

            var playerId = PlayerId.Get(player);
            var dbPlayerJob = _db.Get<PlayerJob>(playerId) ?? new PlayerJob(playerId);

            return dbPlayerJob.JobLevels.ToDictionary(x => x.Key, y => y.Value);
        }
        public void GiveXP(uint player, int xp)
        {
            if (!GetIsPC(player) ||
                GetIsDM(player) ||
                GetIsDMPossessed(player))
                return;

            var playerId = PlayerId.Get(player);
            var dbPlayerJob = _db.Get<PlayerJob>(playerId);
            var job = dbPlayerJob.ActiveJob;
            var level = dbPlayerJob.JobLevels[job];
            var xpRequired = _xp[level];
            var levelsGained = new List<int>();

            dbPlayerJob.JobXP[job] += xp;

            while (dbPlayerJob.JobXP[job] >= xpRequired)
            {
                if (level >= LevelCap)
                {
                    dbPlayerJob.JobXP[job] = xpRequired - 1;
                    break;
                }

                level++;
                dbPlayerJob.JobLevels[job] = level;
                dbPlayerJob.JobXP[job] -= xpRequired;
                xpRequired = _xp[level];

                levelsGained.Add(level);
            }

            _db.Set(dbPlayerJob);

            var definition = GetJobDefinition(job);
            foreach (var gainedLevel in levelsGained)
            {
                _event.PublishEvent(player, new JobEvent.PlayerLeveledUpEvent(definition, gainedLevel));

                var name = GetName(player);
                var levelUpMessage = LocaleString.XAttainsLevelY.ToLocalizedString(name, gainedLevel);
                Messaging.SendMessageNearbyToPlayers(player, levelUpMessage);
            }

            var message = LocaleString.YouEarnedExperience.ToLocalizedString();
            SendMessageToPC(player, message);

            _event.PublishEvent<UIEvent.UIRefreshEvent>(player);
        }

        public void ChangeJob(
            uint player, 
            JobType job,
            List<FeatType> resonanceFeats)
        {
            if (!GetIsPC(player) || GetIsDM(player) || GetIsDMPossessed(player))
                throw new ArgumentException("Only players may change jobs.");

            _inventory.UnequipAllItems(player);

            var definition = GetJobDefinition(job);
            var playerId = PlayerId.Get(player);
            var dbPlayerJob = _db.Get<PlayerJob>(playerId);
            var level = dbPlayerJob.JobLevels[job];
            var currentJob = GetActiveJob(player);
            var featsToAdd = new List<FeatType>();
            var featsToRemove = new List<FeatType>();

            for(var index = dbPlayerJob.ResonanceFeats.Count-1; index >= 0; index--)
            {
                var feat = dbPlayerJob.ResonanceFeats[index];
                CreaturePlugin.RemoveFeat(player, feat);
                dbPlayerJob.ResonanceFeats.Remove(feat);
                featsToRemove.Add(feat);
            }

            foreach (var (_, feat) in currentJob.FeatAcquisitionLevels)
            {
                CreaturePlugin.RemoveFeat(player, feat);
                featsToRemove.Add(feat);
            }

            var jobFeats = definition
                .FeatAcquisitionLevels
                .Where(x => x.Key <= level)
                .Select(s => s.Value)
                .ToList();

            foreach (var feat in jobFeats)
            {
                CreaturePlugin.AddFeatByLevel(player, feat, 1);
                featsToRemove.Add(feat);
            }

            foreach (var feat in resonanceFeats)
            {
                CreaturePlugin.AddFeatByLevel(player, feat, 1);
                dbPlayerJob.ResonanceFeats.Add(feat);
                featsToAdd.Add(feat);
            }

            dbPlayerJob.ActiveJob = job;
            _db.Set(dbPlayerJob);

            var @class = _jobsToClasses[job];
            CreaturePlugin.SetClassByPosition(player, 0, @class);

            foreach (var feat in featsToRemove)
            {
                _event.PublishEvent(player, new JobEvent.JobFeatRemovedEvent(feat));
            }

            foreach (var feat in featsToAdd)
            {
                _event.PublishEvent(player, new JobEvent.JobFeatAddedEvent(feat));
            }

            _event.PublishEvent(player, new JobEvent.PlayerChangedJobEvent((JobDefinitionBase)definition, level));

            var name = definition.Name.ToLocalizedString();
            var message = LocaleString.JobChangedToX.ToLocalizedString(ColorToken.Green(name));
            SendMessageToPC(player, message);
        }

        [ScriptHandler("bread_test1")]
        public void GiveXPTest()
        {
            var player = GetLastUsedBy();
            var bread = OBJECT_SELF;
            var xp = GetLocalInt(bread, "XP");

            GiveXP(player, xp);
        }
    }
}
