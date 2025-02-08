namespace EntJoy
{
    public class ArchetypeShare
    {
        private readonly StructArray<int> sparseEntityIdToArrayIndex = new(32);

        public void Set(int index, int value)
        {
            sparseEntityIdToArrayIndex.SetValue(index, value);
        }

        public int Get(int index)
        {
            return sparseEntityIdToArrayIndex.Get(index);
        }
    }
}