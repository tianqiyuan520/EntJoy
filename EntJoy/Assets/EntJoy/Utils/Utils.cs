namespace EntJoy
{
    public static partial class Utils
    {
        public static bool IsMatch(this Archetype archetype, QueryBuilder queryBuilder)
        {
            if (queryBuilder.All != null && !archetype.HasAllOf(queryBuilder.All))
            {
                return false;
            }

            if (queryBuilder.Any != null && !archetype.HasAnyOf(queryBuilder.Any))
            {
                return false;
            }

            if (queryBuilder.None != null && !archetype.HasNoneOf(queryBuilder.None))
            {
                return false;
            }

            return true;
        }
    }
}