﻿using System;
using System.Collections.Generic;
using System.Linq;
using Anvil.Services;
using NLog;
using XM.Shared.API.NWNX.UtilPlugin;
using XM.Shared.Core.Dialog.Event;
using XM.Shared.Core.EventManagement;

namespace XM.Shared.Core.Dialog.Snippet
{
    [ServiceBinding(typeof(SnippetService))]
    public class SnippetService
    {
        private static readonly Logger _logger = LogManager.GetCurrentClassLogger();

        private readonly Dictionary<string, SnippetDetail> _appearsWhenCommands = new();
        private readonly Dictionary<string, SnippetDetail> _actionsTakenCommands = new();

        [Inject]
        public IList<ISnippetListDefinition> Definitions { get; set; }

        public SnippetService(XMEventService @event)
        {
            @event.Subscribe<XMEvent.OnCacheDataBefore>(OnCacheDataBefore);
        }

        private void OnCacheDataBefore()
        {
            foreach (var definition in Definitions)
            {
                var snippets = definition.BuildSnippets();

                foreach (var (key, snippet) in snippets)
                {
                    if (snippet.ConditionAction != null)
                    {
                        _appearsWhenCommands.Add(key, snippet);
                    }

                    if (snippet.ActionsTakenAction != null)
                    {
                        _actionsTakenCommands.Add(key, snippet);
                    }

                }
            }

            _logger.Info($"Loaded {_actionsTakenCommands.Count} action snippets.");
            _logger.Info($"Loaded {_appearsWhenCommands.Count} condition snippets.");
        }

        /// <summary>
        /// When a conversation node with this script assigned in the "Appears When" event is run,
        /// check for any conversation conditions and process them.
        /// </summary>
        /// <returns></returns>
        [ScriptHandler(DialogEventScript.AppearScript)]
        [ScriptHandler(DialogEventScript.AppearsScript)]
        [ScriptHandler(DialogEventScript.ConditionScript)]
        [ScriptHandler(DialogEventScript.ConditionsScript)]
        public bool ConversationAppearsWhen()
        {
            var player = GetPCSpeaker();
            return ProcessConditions(player);
        }

        /// <summary>
        /// When a conversation node with this script assigned in the "Actions Taken" event is run,
        /// check for any conversation actions and process them.
        /// </summary>
        [ScriptHandler(DialogEventScript.ActionScript)]
        [ScriptHandler(DialogEventScript.ActionsScript)]
        public void ConversationAction()
        {
            var player = GetPCSpeaker();
            ProcessActions(player);
        }

        /// <summary>
        /// Handles processing condition commands.
        /// If any of the conditions fail, false will be returned.
        /// </summary>
        /// <param name="player">The player running the conditions.</param>
        /// <returns>true if all commands passed successfully, false otherwise</returns>
        private bool ProcessConditions(uint player)
        {
            foreach (var condition in _appearsWhenCommands)
            {
                var notConditionEnabled = false;

                // Check for "not" condition first.
                if (UtilPlugin.GetScriptParamIsSet("!" + condition.Key))
                {
                    notConditionEnabled = true;
                }
                // If we can't find either condition, exit.
                else if (!UtilPlugin.GetScriptParamIsSet(condition.Key)) continue;

                var conditionKey = notConditionEnabled ? "!" + condition.Key : condition.Key;
                var param = GetScriptParam(conditionKey);
                var args = param.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
                var snippetName = condition.Key;

                // The first command that fails will result in failure.
                var commandResult = _appearsWhenCommands[snippetName].ConditionAction(player, args.ToArray());
                
                // "Not" conditions check for the opposite condition.
                if (notConditionEnabled && commandResult)
                    return false;

                // Normal conditions
                if (!notConditionEnabled && !commandResult) return false;
            }

            return true;
        }

        /// <summary>
        /// Handles processing action commands.
        /// </summary>
        /// <param name="player">The player to run the commands against</param>
        private void ProcessActions(uint player)
        {
            foreach (var action in _actionsTakenCommands)
            {
                if (!UtilPlugin.GetScriptParamIsSet(action.Key)) continue;

                var param = GetScriptParam(action.Key);
                var args = param.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
                var commandText = action.Key;

                _actionsTakenCommands[commandText].ActionsTakenAction(player, args.ToArray());
            }
        }

    }
}
