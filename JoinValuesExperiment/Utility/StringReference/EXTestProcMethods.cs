using PX.Data;

namespace JoinValuesExperiment
{
	public class EXTestProcMethods
	{

		public const string WhereChainRecursive = "R";
		public const string WhereChainNonRecursive = "N";
		public const string Values = "V";

		public class ListAttribute : PXStringListAttribute
		{
			public ListAttribute() : base
			(
				new string[]
				{
					WhereChainRecursive,
					WhereChainNonRecursive,
					Values
				},
				new string[]
				{
					"WhereChain Recursive",
					"WhereChain NonRecursive",
					"Values"
				}
			)
			{
			}
		}

	}
}