﻿namespace NWN.Xenomech.Core
{
    public interface ICoreFunctionHandler
    {
        uint ObjectSelf { get; set; }

        void ClosureAssignCommand(uint obj, Action func);
        void ClosureDelayCommand(uint obj, float duration, Action func);
        void ClosureActionDoCommand(uint obj, Action func);
    }
}