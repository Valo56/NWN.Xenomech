﻿namespace XM.Quest.Objective
{
    internal interface IQuestObjective
    {
        void Initialize(uint player, string questId);
        void Advance(uint player, string questId);
        bool IsComplete(uint player, string questId);
        string GetCurrentStateText(uint player, string questId);
    }
}
