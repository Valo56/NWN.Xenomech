﻿using Anvil.API;
using XM.Shared.API.Constants;
using XM.Shared.Core.Authorization;
using XM.Shared.Core.Localization;

namespace XM.Shared.Core.ChatCommand
{
    public class ChatCommandDetail
    {
        /// <summary>
        /// This is a delegate which is executed when a chat command is run.
        /// </summary>
        /// <param name="user">The user activating the chat command</param>
        /// <param name="target">The target of the chat command.</param>
        /// <param name="targetLocation">The target location of the chat command.</param>
        /// <param name="args">Any arguments sent in by the user. Be sure to validate these in the ValidateArgumentsDelegate.</param>
        public delegate void ExecuteChatCommandDelegate(uint user, uint target, Location targetLocation, params string[] args);

        /// <summary>
        /// This is a delegate which is executed when a chat command is validated.
        /// </summary>
        /// <param name="user">The user activating the chat command.</param>
        /// <param name="args">Any arguments sent in by the user. Be sure to validate these here.</param>
        /// <returns>If successful, return a null or empty string. Otherwise, return the error message.</returns>
        public delegate string ValidateArgumentsDelegate(uint user, params string[] args);

        public LocaleString Description { get; set; } = LocaleString.Empty;
        public AuthorizationLevel Authorization { get; set; }
        public ExecuteChatCommandDelegate DoAction { get; set; }
        public ValidateArgumentsDelegate ValidateArguments { get; set; }
        public bool RequiresTarget { get; set; }
        public ObjectType ValidTargetTypes { get; set; }
        public bool IsEmote { get; set; }
        public AnimationType EmoteAnimation { get; set; }
        public bool IsEmoteLooping { get; set; }
        public bool AvailableToAllOnTestEnvironment { get; set; }
    }
}
