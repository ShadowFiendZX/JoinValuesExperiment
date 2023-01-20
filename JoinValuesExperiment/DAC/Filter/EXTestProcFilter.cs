using PX.Data;
using PX.Data.BQL;
using System;

namespace JoinValuesExperiment
{
	[Serializable]
	[PXHidden]
	public class EXTestProcFilter : IBqlTable
	{

		#region ProcessingMethod
		public abstract class processingMethod : BqlString.Field<processingMethod>
		{
		}
		[PXString(1, IsFixed = true)]
		[EXTestProcMethods.List]
		[PXDefault(EXTestProcMethods.WhereChainRecursive)]
		public string ProcessingMethod { get; set; }
		#endregion

	}
}