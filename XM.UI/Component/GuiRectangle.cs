﻿
using XM.API.BaseTypes;
using XM.API.NUI;

namespace XM.UI.Component
{
    public class GuiRectangle
    {
        /// <summary>
        /// The X coordinate
        /// </summary>
        public float X { get; set; }

        /// <summary>
        /// The Y coordinate
        /// </summary>
        public float Y { get; set; }

        /// <summary>
        /// The width value.
        /// </summary>
        public float Width { get; set; }

        /// <summary>
        /// The height value.
        /// </summary>
        public float Height { get; set; }

        public GuiRectangle(float x, float y, float width, float height)
        {
            X = x;
            Y = y;
            Width = width;
            Height = height;
        }

        /// <summary>
        /// Converts the rectangle to a Json object readable by NWN.
        /// </summary>
        /// <returns>Json representing the rectangle</returns>
        public Json ToJson()
        {
            return Nui.Rect(X, Y, Width, Height);
        }
    }
}