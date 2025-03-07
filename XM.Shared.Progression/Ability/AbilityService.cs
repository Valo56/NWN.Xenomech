﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Anvil.API;
using Anvil.Services;
using NWN.Core.NWNX;
using XM.Progression.Ability.Telegraph;
using XM.Progression.Event;
using XM.Progression.Job;
using XM.Progression.Job.Entity;
using XM.Progression.Recast;
using XM.Progression.Skill;
using XM.Progression.Stat;
using XM.Progression.Stat.Entity;
using XM.Shared.API.Constants;
using XM.Shared.Core;
using XM.Shared.Core.Activity;
using XM.Shared.Core.Data;
using XM.Shared.Core.EventManagement;
using XM.Shared.Core.Localization;
using CreaturePlugin = XM.Shared.API.NWNX.CreaturePlugin.CreaturePlugin;

namespace XM.Progression.Ability
{
    [ServiceBinding(typeof(AbilityService))]
    [ServiceBinding(typeof(IInitializable))]
    public class AbilityService: IInitializable
    {
        private readonly Dictionary<FeatType, AbilityDetail> _abilities = new();
        private readonly Dictionary<FeatType, int> _abilitiesByLevel = new();

        private readonly DBService _db;
        private readonly ActivityService _activity;
        private readonly StatService _stat;
        private readonly RecastService _recast;
        private readonly JobService _job;
        private readonly IList<IAbilityListDefinition> _abilityDefinitions;
        private readonly Dictionary<JobType, List<FeatType>> _abilitiesByJob = new();
        private readonly XMEventService _event;
        private readonly TelegraphService _telegraph;
        private readonly SkillService _skill;

        public AbilityService(
            DBService db,
            ActivityService activity,
            StatService stat,
            RecastService recast,
            JobService job,
            IList<IAbilityListDefinition> abilityDefinitions,
            XMEventService @event,
            TelegraphService telegraph,
            SkillService skill)
        {
            _db = db;
            _activity = activity;
            _stat = stat;
            _recast = recast;
            _job = job;
            _abilityDefinitions = abilityDefinitions;
            _event = @event;
            _telegraph = telegraph;
            _skill = skill;

            CacheAbilities();

            RegisterEvents();
            SubscribeEvents();
        }

        private void RegisterEvents()
        {
            _event.RegisterEvent<AbilityEvent.OnQueueWeaponSkill>(ProgressionEventScript.OnQueueWeaponSkillScript);
            _event.RegisterEvent<AbilityEvent.OnAbilitiesRegistered>(ProgressionEventScript.OnRegisterAbilityScript);
        }

        private void SubscribeEvents()
        {
            _event.Subscribe<NWNXEvent.OnUseFeatBefore>(UseAbility);
            _event.Subscribe<JobEvent.JobFeatAddedEvent>(AddJobFeat);
            _event.Subscribe<JobEvent.JobFeatRemovedEvent>(RemoveJobFeat);
            _event.Subscribe<JobEvent.PlayerLeveledUpEvent>(ApplyLevelUp);
        }

        public void Init()
        {
            LoadAbilityDescriptions();
            PublishAbilities();
        }

        private void LoadAbilityDescriptions()
        {
            foreach (var (type, detail) in _abilities)
            {
                var tlkId = detail.Description.GetTlkId();
                var description = BuildAbilityDescription(type);
                SetTlkOverride(tlkId, description);
            }
        }

        private void PublishAbilities()
        {
            var module = GetModule();
            var abilityList = _abilities.Values.ToList();
            var @event = new AbilityEvent.OnAbilitiesRegistered(abilityList);
            _event.PublishEvent(module, @event);
        }

        private void CacheAbilities()
        {
            var jobs = _job.GetAllJobDefinitions();

            foreach (var definition in _abilityDefinitions)
            {
                var abilities = definition.BuildAbilities();

                foreach (var (feat, ability) in abilities)
                {
                    ability.IconResref = Get2DAString("feat", "ICON", (int)feat);
                    _abilities[feat] = ability;

                    foreach (var (type, job) in jobs)
                    {
                        var orderedFeats = job.FeatAcquisitionLevels
                            .OrderBy(kvp => kvp.Key)
                            .Select(kvp => kvp.Value);

                        foreach (var featEntry in orderedFeats)
                        {
                            if (featEntry == feat)
                            {
                                if (!_abilitiesByJob.ContainsKey(type))
                                    _abilitiesByJob[type] = new List<FeatType>();

                                _abilitiesByJob[type].Add(feat);

                                if (_abilitiesByLevel.ContainsKey(feat))
                                {
                                    throw new Exception($"Feat '{feat}' has been registered across multiple jobs. Feats can only be registered to one job.");
                                }

                                _abilitiesByLevel[feat] = job.GetFeatAcquiredLevel(feat);
                            }
                        }
                    }
                }
            }

            Console.WriteLine($"Loaded {_abilities.Count} abilities.");
        }

        internal List<FeatType> GetAbilityFeatsByJob(JobType jobType)
        {
            return _abilitiesByJob[jobType].ToList();
        }

        public AbilityDetail GetAbilityDetail(FeatType featType)
        {
            if (!_abilities.ContainsKey(featType))
                throw new KeyNotFoundException($"Feat '{featType}' is not registered to an ability.");

            return _abilities[featType];
        }

        private bool HasManafont(uint target)
        {
            return GetLocalBool(target, "MANAFONT");
        }

        public bool CanUseAbility(
            uint activator,
            uint target,
            FeatType feat,
            Location targetLocation)
        {
            if (!_abilities.ContainsKey(feat))
                return false;

            var ability = GetAbilityDetail(feat);

            if (GetIsPC(activator) && 
                !GetIsDM(activator) && 
                !GetIsDMPossessed(activator))
            {
                var level = _stat.GetLevel(activator);
                var job = _job.GetActiveJob(activator);
                var levelAcquired = job.GetFeatAcquiredLevel(feat);

                if (level < levelAcquired)
                {
                    SendMessageToPC(activator, LocaleString.InsufficientLevel.ToLocalizedString());
                    return false;
                }
            }

            // Activator is dead.
            if (GetCurrentHitPoints(activator) <= 0)
            {
                SendMessageToPC(activator, LocaleString.YouAreDead.ToLocalizedString());
                return false;
            }

            // Not commandable
            if (!GetCommandable(activator))
            {
                SendMessageToPC(activator, LocaleString.YouCannotTakeActionsAtThisTime.ToLocalizedString());
                return false;
            }

            // Must be within line of sight.
            if (GetIsObjectValid(target) && !LineOfSightObject(activator, target))
            {
                SendMessageToPC(activator, LocaleString.YouCannotSeeYourTarget.ToLocalizedString());
                return false;
            }

            // Must not be busy
            if (_activity.IsBusy(activator))
            {
                SendMessageToPC(activator, LocaleString.YouAreBusy.ToLocalizedString());
                return false;
            }

            // Range check.
            if (GetDistanceBetween(activator, target) > ability.MaxRange)
            {
                SendMessageToPC(activator, LocaleString.YouAreOutOfRangeThisAbilityHasARangeOfXMeters.ToLocalizedString(ability.MaxRange));
                return false;
            }

            // Hostility check
            if (GetIsObjectValid(target) && !GetIsReactionTypeHostile(target, activator) && ability.IsHostileAbility)
            {
                SendMessageToPC(activator, LocaleString.YouMayOnlyUseThisAbilityOnEnemies.ToLocalizedString());
                return false;
            }

            // Weapon check
            if (ability.WeaponSkillType != SkillType.Invalid)
            {
                var weapon = GetItemInSlot(InventorySlotType.RightHand, activator);
                var skill = _skill.GetSkillOfWeapon(weapon);
                if (skill != ability.WeaponSkillType)
                {
                    SendMessageToPC(activator, LocaleString.IncorrectWeaponEquippedForThisAbility.ToLocalizedString());
                    return false;
                }

                if (GetIsPC(activator))
                {
                    var requiredLevel = ability.SkillLevelRequired;
                    var level = _skill.GetSkillLevel(activator, skill);

                    if (level < requiredLevel)
                    {
                        SendMessageToPC(activator, LocaleString.InsufficientLevel.ToLocalizedString());
                        return false;
                    }
                }
            }

            // EP check
            if (!HasManafont(activator) && _stat.GetCurrentEP(activator) < ability.EPRequired)
            {
                SendMessageToPC(activator, LocaleString.InsufficientEP.ToLocalizedString());
                return false;
            }

            // TP check
            if (_stat.GetCurrentTP(activator) < ability.TPRequired)
            {
                SendMessageToPC(activator, LocaleString.InsufficientTP.ToLocalizedString());
                return false;
            }

            // Perk-specific custom validation logic.
            var customValidationResult = ability.CustomValidation == null 
                ? string.Empty 
                : ability.CustomValidation(activator, target, targetLocation);
            if (!string.IsNullOrWhiteSpace(customValidationResult))
            {
                SendMessageToPC(activator, customValidationResult);
                return false;
            }

            // Check if ability is on a recast timer still.
            var (isOnRecast, timeToWait) = _recast.IsOnRecastDelay(activator, ability.RecastGroup);
            if (isOnRecast)
            {
                SendMessageToPC(activator, LocaleString.ThisAbilityCanBeUsedIn.ToLocalizedString(timeToWait));
                return false;
            }

            return true;
        }
        public bool IsFeatRegistered(FeatType featType)
        {
            return _abilities.ContainsKey(featType);
        }

        // Variable names for queued abilities.
        private const string ActiveActionId = "ACTIVE_ABILITY_ID";
        private const string ActiveAbilityFeatId = "ACTIVE_ABILITY_FEAT_ID";

        private void UseAbility(uint caster)
        {
            var target = StringToObject(EventsPlugin.GetEventData("TARGET_OBJECT_ID"));
            var targetArea = StringToObject(EventsPlugin.GetEventData("AREA_OBJECT_ID"));
            var targetPosition = Vector(
                (float)Convert.ToDouble(EventsPlugin.GetEventData("TARGET_POSITION_X")),
                (float)Convert.ToDouble(EventsPlugin.GetEventData("TARGET_POSITION_Y")),
                (float)Convert.ToDouble(EventsPlugin.GetEventData("TARGET_POSITION_Z"))
            );

            // If we have a valid target, use its position
            if (GetIsObjectValid(target))
            {
                targetPosition = GetPosition(target);
            }

            var targetLocation = Location(targetArea, targetPosition, 0.0f);

            var feat = (FeatType)Convert.ToInt32(EventsPlugin.GetEventData("FEAT_ID"));
            if (!IsFeatRegistered(feat)) return;
            var ability = GetAbilityDetail(feat);

            // Weapon skills & queued attacks are queued for the next time the activator's attack lands on an enemy.
            if (ability.ActivationType == AbilityActivationType.QueuedAttack ||
                ability.ActivationType == AbilityActivationType.WeaponSkill)
            {
                if (CanUseAbility(caster, target, feat, targetLocation))
                {
                    if (ability.DisplaysActivationMessage)
                    {
                        var activator = ability.RetargetActivatorAction?.Invoke(caster) ?? caster;
                        Messaging.SendMessageNearbyToPlayers(caster, LocaleString.PlayerQueuesAbilityForTheNextAttack.ToLocalizedString(GetName(activator), ability.Name.ToLocalizedString()));
                    }
                    QueueAbility(caster, ability, feat);
                }
            }
            // Toggle abilities
            else if (ability.ActivationType == AbilityActivationType.Toggled &&
                     ability.AbilityIsToggledAction(caster))
            {
                var activator = ability.RetargetActivatorAction?.Invoke(caster) ?? caster;
                ability.AbilityToggleAction?.Invoke(activator, false);
            }
            // All other abilities are funneled through the same process.
            else
            {
                if (CanUseAbility(caster, target, feat, targetLocation))
                {
                    if (GetIsObjectValid(target))
                    {
                        if (ability.DisplaysActivationMessage)
                            Messaging.SendMessageNearbyToPlayers(caster, LocaleString.PlayerReadiesAbilityOnTarget.ToLocalizedString(GetName(caster), ability.Name.ToLocalizedString(), GetName(target)));
                    }
                    else
                    {
                        if (ability.DisplaysActivationMessage)
                            Messaging.SendMessageNearbyToPlayers(caster, LocaleString.PlayerReadiesAbility.ToLocalizedString(GetName(caster), ability.Name.ToLocalizedString()));
                    }

                    ActivateAbility(caster, target, feat, ability, targetLocation);
                }
            }
        }

        private void ActivateAbility(
            uint activator,
            uint target,
            FeatType feat,
            AbilityDetail ability,
            Location targetLocation)
        {
            var telegraphId = string.Empty;

            float CalculateActivationDelay()
            {
                var abilityDelay = ability.ActivationDelay?.Invoke(activator, target) ?? 0.0f;
                return abilityDelay;
            }

            // Handles displaying animation and visual effects.
            void ProcessAnimationAndVisualEffects(float delay)
            {
                // Force out of stealth
                if (GetActionMode(activator, ActionModeType.Stealth))
                    SetActionMode(activator, ActionModeType.Stealth, false);

                AssignCommand(activator, () => ClearAllActions());
                TurnToFaceObject(target, activator);

                // Display a casting visual effect if one has been specified.
                if (ability.ActivationVisualEffect != VisualEffectType.None)
                {
                    var vfx = TagEffect(EffectVisualEffect(ability.ActivationVisualEffect), "ACTIVATION_VFX");
                    ApplyEffectToObject(DurationType.Temporary, vfx, activator, delay + 0.2f);
                }

                // Casted types play an animation of casting.
                if (ability.ActivationType == AbilityActivationType.Casted &&
                    ability.AnimationType != AnimationType.Invalid)
                {
                    var animationLength = delay - 0.2f;
                    if (animationLength < 0f)
                        animationLength = 0f;

                    AssignCommand(activator, () => ActionPlayAnimation(ability.AnimationType, 1.0f, animationLength));
                }
            }

            // Recursive function which checks if player has moved since starting the casting.
            void CheckForActivationInterruption(string activationId, Vector3 originalPosition)
            {
                if (!GetIsPC(activator)) 
                    return;

                // Completed abilities should no longer run.
                var status = GetLocalInt(activator, activationId);
                if (status == (int)ActivationStatus.Completed || status == (int)ActivationStatus.Invalid) 
                    return;

                var currentPosition = GetPosition(activator);

                if (currentPosition.X != originalPosition.X ||
                    currentPosition.Y != originalPosition.Y ||
                    currentPosition.Z != originalPosition.Z)
                {
                    _activity.ClearBusy(activator);
                    RemoveEffectByTag(activator, "ACTIVATION_VFX");
                    PlayerPlugin.StopGuiTimingBar(activator, string.Empty);
                    Messaging.SendMessageNearbyToPlayers(activator, LocaleString.PlayersAbilityHasBeenInterrupted.ToLocalizedString(GetName(activator)));
                    SetLocalInt(activator, activationId, (int)ActivationStatus.Interrupted);
                    _telegraph.CancelTelegraph(telegraphId);
                    return;
                }

                DelayCommand(0.5f, () => CheckForActivationInterruption(activationId, originalPosition));
            }

            // This method is called after the delay of the ability has finished.
            bool CompleteActivation(uint caster, string activationId)
            {
                _activity.ClearBusy(caster);

                // Moved during casting or activator died. Cancel the activation.
                if (GetLocalInt(caster, activationId) == (int)ActivationStatus.Interrupted || GetCurrentHitPoints(caster) <= 0)
                    return false;

                if (!CanUseAbility(caster, target, feat, targetLocation))
                    return false;

                DeleteLocalInt(caster, activationId);

                ApplyRequirementEffects(caster, ability);

                return true;
            }

            void ExecuteImpact()
            {
                ability.ImpactAction?.Invoke(activator, target, targetLocation);
                ability.AbilityToggleAction?.Invoke(activator, true);
            }

            // Begin the main process
            var activationId = Guid.NewGuid().ToString();
            var activationDelay = CalculateActivationDelay();
            var recastDelay = ability.RecastDelay?.Invoke(activator) ?? 0f;
            var position = GetPosition(activator);
            ProcessAnimationAndVisualEffects(activationDelay);
            SetLocalInt(activator, activationId, (int)ActivationStatus.Started);
            CheckForActivationInterruption(activationId, position);

            var executeImpact = ability.ActivationAction == null
                ? true
                : ability.ActivationAction?.Invoke(activator, target, targetLocation);

            if (executeImpact == true)
            {
                if (GetIsPC(activator))
                {
                    if (activationDelay > 0.0f)
                    {
                        PlayerPlugin.StartGuiTimingBar(activator, activationDelay, string.Empty);
                    }
                }

                _activity.SetBusy(activator, ActivityStatusType.AbilityActivation);

                if (ability.TelegraphAction != null && 
                    ability.TelegraphType != TelegraphType.None &&
                    ability.ActivationType != AbilityActivationType.WeaponSkill)
                {
                    HandleTelegraphAbility(
                        activator, 
                        activationDelay, 
                        ability, 
                        targetLocation,
                        (telegrapher, creatures) =>
                        {
                            if (CompleteActivation(telegrapher, activationId))
                            {
                                _recast.ApplyRecastDelay(telegrapher, ability.RecastGroup, recastDelay, false);
                                ability.TelegraphAction(telegrapher, creatures, targetLocation);
                            }
                        });
                }
                else
                {
                    DelayCommand(activationDelay, () =>
                    {
                        if (CompleteActivation(activator, activationId))
                        {
                            ExecuteImpact();
                            _recast.ApplyRecastDelay(activator, ability.RecastGroup, recastDelay, false);
                        }
                    });
                }

                // If currently attacking a target, re-attack it after the end of the activation period.
                // This mitigates the issue where a melee fighter's combat is disrupted for using an ability.
                if (GetCurrentAction(activator) == ActionType.AttackObject)
                {
                    var attackTarget = GetAttackTarget(activator);
                    DelayCommand(activationDelay + 0.1f, () =>
                    {
                        AssignCommand(activator, () => ActionAttack(attackTarget));
                    });
                }
            }
        }

        public string HandleTelegraphAbility(
            uint activator, 
            float duration, 
            AbilityDetail ability, 
            Location targetLocation,
            ApplyTelegraphEffect action)
        {
            var telegraphPosition = targetLocation == LOCATION_INVALID
                ? GetPosition(activator)
                : GetPositionFromLocation(targetLocation);
            var telegraphFacing = targetLocation == LOCATION_INVALID
                ? GetFacing(activator)
                : GetFacingFromLocation(targetLocation);

            var telegraphId = _telegraph.CreateTelegraph(
                activator,
                telegraphPosition,
                telegraphFacing,
                ability.TelegraphSize,
                duration,
                ability.IsHostileAbility,
                ability.TelegraphType,
                action);

            return telegraphId;
        }

        private void QueueAbility(uint caster, AbilityDetail ability, FeatType feat)
        {
            var abilityId = Guid.NewGuid().ToString();
            // Assign local variables which will be picked up on the next weapon OnHit event by this player.
            var activator = ability.RetargetActivatorAction?.Invoke(caster) ?? caster;
            SetLocalString(activator, ActiveActionId, abilityId);
            SetLocalInt(activator, ActiveAbilityFeatId, (int)feat);

            ApplyRequirementEffects(caster, ability);

            var abilityRecastDelay = ability.RecastDelay?.Invoke(caster) ?? 0.0f;
            _recast.ApplyRecastDelay(caster, ability.RecastGroup, abilityRecastDelay, false);

            // Activator must attack within 30 seconds after queueing or else it wears off.
            DelayCommand(30.0f, () =>
            {
                DequeueWeaponAbility(activator, ability.DisplaysActivationMessage);
            });

            if (ability.ActivationType == AbilityActivationType.WeaponSkill)
            {
                ApplyEffectToObject(DurationType.Instant, EffectVisualEffect(VisualEffectType.ImpFortitudeSavingThrowUse), caster);
                _event.PublishEvent<AbilityEvent.OnQueueWeaponSkill>(caster);
            }
        }

        private void DequeueWeaponAbility(uint target, bool sendMessage = true)
        {
            var actionId = GetLocalString(target, ActiveActionId);
            if (string.IsNullOrWhiteSpace(actionId))
                return;

            var featId = GetLocalInt(target, ActiveAbilityFeatId);
            if (featId == 0)
                return;

            var featType = (FeatType)featId;
            var abilityDetail = GetAbilityDetail(featType);

            // Remove the local variables.
            DeleteLocalString(target, ActiveActionId);
            DeleteLocalInt(target, ActiveAbilityFeatId);

            // Notify the activator and nearby players
            SendMessageToPC(target, LocaleString.YourWeaponAbilityXIsNoLongerQueued.ToLocalizedString(abilityDetail.Name.ToLocalizedString()));

            if (sendMessage)
                Messaging.SendMessageNearbyToPlayers(target, LocaleString.PlayerNoLongerHasWeaponAbilityXReadied.ToLocalizedString(GetName(target), abilityDetail.Name.ToLocalizedString()));
        }

        public AbilityDetail GetQueuedAbility(uint attacker)
        {
            var featType = (FeatType)GetLocalInt(attacker, ActiveAbilityFeatId);
            if (!IsFeatRegistered(featType))
                return null;

            return _abilities[featType];
        }

        public void ProcessQueuedAbility(uint attacker, uint defender)
        {
            var featType = (FeatType)GetLocalInt(attacker, ActiveAbilityFeatId);

            if (!IsFeatRegistered(featType))
                return;

            var ability = GetQueuedAbility(attacker);
            if (ability.ActivationType != AbilityActivationType.QueuedAttack)
                return;

            ability.ImpactAction?.Invoke(attacker, defender, GetLocation(defender));

            DeleteLocalString(attacker, ActiveActionId);
            DeleteLocalInt(attacker, ActiveAbilityFeatId);
        }

        public void ProcessWeaponAbility(uint attacker, uint defender, float telegraphTime)
        {
            var wsAbility = GetQueuedAbility(attacker);
            if (wsAbility == null ||
                wsAbility.ActivationType != AbilityActivationType.WeaponSkill)
                return;

            var targetLocation = GetLocation(attacker);
            if (wsAbility.TelegraphType == TelegraphType.None)
            {
                wsAbility.ImpactAction?.Invoke(attacker, defender, targetLocation);
            }
            else
            {
                HandleTelegraphAbility(
                    attacker,
                    telegraphTime,
                    wsAbility,
                    targetLocation,
                    (telegrapher, creatures) =>
                    {
                        wsAbility.TelegraphAction?.Invoke(telegrapher, creatures, targetLocation);
                    });
            }


            DeleteLocalString(attacker, ActiveActionId);
            DeleteLocalInt(attacker, ActiveAbilityFeatId);
        }

        private void ApplyRequirementEffects(uint activator, AbilityDetail ability)
        {
            if(!HasManafont(activator))
                _stat.ReduceEP(activator, ability.EPRequired);

            _stat.ReduceTP(activator, ability.TPRequired);
        }

        private void AddJobFeat(uint player)
        {
            var data = _event.GetEventData<JobEvent.JobFeatAddedEvent>();
            if (!_abilities.ContainsKey(data.Feat))
                return;

            var playerId = PlayerId.Get(player);
            var dbPlayerStat = _db.Get<PlayerStat>(playerId);
            var ability = _abilities[data.Feat];

            dbPlayerStat.AbilityStats.Add(data.Feat, ability.StatGroup);
            _db.Set(dbPlayerStat);

            ability.AbilityEquippedAction?.Invoke(player);
        }
        private void RemoveJobFeat(uint player)
        {
            var data = _event.GetEventData<JobEvent.JobFeatRemovedEvent>();
            if (!_abilities.ContainsKey(data.Feat))
                return;

            var playerId = PlayerId.Get(player);
            var dbPlayerStat = _db.Get<PlayerStat>(playerId);
            var ability = _abilities[data.Feat];

            dbPlayerStat.AbilityStats.Remove(data.Feat);
            _db.Set(dbPlayerStat);

            ability.AbilityUnequippedAction?.Invoke(player);
        }

        internal List<FeatType> GetPlayerResonanceAbilities(uint player)
        {
            var playerId = PlayerId.Get(player);
            var dbPlayerJob = _db.Get<PlayerJob>(playerId);

            return dbPlayerJob.ResonanceFeats.ToList();
        }

        public int GetLevelAcquired(FeatType feat)
        {
            if (!_abilitiesByLevel.ContainsKey(feat))
                return 999;

            return _abilitiesByLevel[feat];
        }

        private void ApplyLevelUp(uint player)
        {
            var data = _event.GetEventData<JobEvent.PlayerLeveledUpEvent>();
            var definition = data.Definition;
            var level = data.Level;

            var feat = definition.FeatAcquisitionLevels.ContainsKey(level)
                ? definition.FeatAcquisitionLevels[level]
                : FeatType.Invalid;

            if (feat == FeatType.Invalid)
                return;

            if (!_abilities.ContainsKey(feat))
                return;

            var ability = _abilities[feat];
            CreaturePlugin.AddFeatByLevel(player, feat, 1);
            var name = ability.Name.ToLocalizedString();
            var message = LocaleString.AbilityAcquiredX.ToLocalizedString(name);
            SendMessageToPC(player, message);

            _event.PublishEvent(player, new JobEvent.JobFeatAddedEvent(feat));
        }

        private string BuildAbilityDescription(FeatType feat)
        {
            var module = GetModule();
            var ability = GetAbilityDetail(feat);
            var description = ability.Description.ToLocalizedString();
            var type = string.Empty;
            var activationDelay = 
                ability.ActivationDelay?.Invoke(module, module) ?? 0f;
            var recastDelay = ability.RecastDelay?.Invoke(module) ?? 0f;
            var ep = ability.EPRequired;
            var tp = ability.TPRequired;
            var element = string.Empty;
            var range = ability.MaxRange;
            var resonanceCost = ability.ResonanceCost;
            var skill = string.Empty;

            switch (ability.ActivationType)
            {
                case AbilityActivationType.Casted:
                    type = LocaleString.Casted.ToLocalizedString();
                    break;
                case AbilityActivationType.QueuedAttack:
                    type = LocaleString.QueuedAttack.ToLocalizedString();
                    break;
                case AbilityActivationType.Stance:
                    type = LocaleString.Stance.ToLocalizedString();
                    break;
                case AbilityActivationType.Aura:
                    type = LocaleString.Aura.ToLocalizedString();
                    break;
                case AbilityActivationType.Toggled:
                    type = LocaleString.Toggled.ToLocalizedString();
                    break;
                case AbilityActivationType.PassiveWeaponSkill:
                    type = LocaleString.PassiveWeaponSkill.ToLocalizedString();
                    break;
                case AbilityActivationType.WeaponSkill:
                    type = LocaleString.WeaponSkill.ToLocalizedString();
                    break;
                default:
                    type = LocaleString.Unknown.ToLocalizedString();
                    break;
            }

            switch (ability.ResistType)
            {
                case ResistType.Fire:
                    element = LocaleString.Fire.ToLocalizedString();
                    break;
                case ResistType.Ice:
                    element = LocaleString.Ice.ToLocalizedString();
                    break;
                case ResistType.Wind:
                    element = LocaleString.Wind.ToLocalizedString();
                    break;
                case ResistType.Earth:
                    element = LocaleString.Earth.ToLocalizedString();
                    break;
                case ResistType.Lightning:
                    element = LocaleString.Lightning.ToLocalizedString();
                    break;
                case ResistType.Water:
                    element = LocaleString.Water.ToLocalizedString();
                    break;
                case ResistType.Light:
                    element = LocaleString.Light.ToLocalizedString();
                    break;
                case ResistType.Darkness:
                    element = LocaleString.Darkness.ToLocalizedString();
                    break;
                case ResistType.Mind:
                    element = LocaleString.Mind.ToLocalizedString();
                    break;
                default:
                    element = LocaleString.Unknown.ToLocalizedString();
                    break;
            }

            if (ability.WeaponSkillType != SkillType.Invalid)
            {
                var skillDefinition = _skill.GetSkillDefinition(ability.WeaponSkillType);
                var level = ability.SkillLevelRequired;
                skill = $"{skillDefinition.Name.ToLocalizedString()} {LocaleString.Lv.ToLocalizedString()} {level}";
            }

            var abilityText = LocaleString.TypeX.ToLocalizedString(type) + "\n";

            if (activationDelay > 0f)
                abilityText += LocaleString.ActivationDelayX.ToLocalizedString(activationDelay.ToString("F1")) + "\n";

            if (recastDelay > 0f)
                abilityText += LocaleString.RecastDelayX.ToLocalizedString(recastDelay.ToString("F1")) + "\n";

            if (ep > 0)
                abilityText += LocaleString.EPX.ToLocalizedString(ep) + "\n";

            if (tp > 0)
                abilityText += LocaleString.TPX.ToLocalizedString(tp) + "\n";

            if (ability.ResistType != ResistType.Invalid)
                abilityText += LocaleString.ElementX.ToLocalizedString(element) + "\n";

            if (range > 0f)
                abilityText += LocaleString.RangeX.ToLocalizedString(range.ToString("F1")) + "\n";

            if(resonanceCost > 0)
                abilityText += LocaleString.ResonanceCostX.ToLocalizedString(resonanceCost) + "\n";

            if (!string.IsNullOrWhiteSpace(skill))
                abilityText += LocaleString.SkillX.ToLocalizedString(skill) + "\n";

            abilityText += "\n" + description;

            return abilityText;
        }
    }
}
