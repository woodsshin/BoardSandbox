namespace BoardSandbox.Core.Mods
{
    // Where a mod comes from. A list of games shows them in this order.
    public enum ModOrigin
    {
        BuiltIn,
        Local,
        Workshop
    }

    public static class ModOriginExtensions
    {
        public static string Label(this ModOrigin origin)
        {
            switch (origin)
            {
                case ModOrigin.BuiltIn:
                    return "Built-in";
                case ModOrigin.Local:
                    return "Local";
                case ModOrigin.Workshop:
                    return "Workshop";
                default:
                    return origin.ToString();
            }
        }
    }
}
