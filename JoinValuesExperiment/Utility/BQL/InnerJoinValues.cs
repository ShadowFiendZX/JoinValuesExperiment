using PX.Data;
using PX.DbServices.QueryObjectModel;
using System;

namespace JoinValuesExperiment
{
	/// <summary>
	/// This class is just meant to illustrate what I imagine the implementation would look like.
	/// Acumatica's inner query generation logic is convoluted, and nonpublic so I can't mock up the functionality.
	/// </summary>
	/// <typeparam name="TValueWrapper"></typeparam>
	/// <typeparam name="TOn"></typeparam>
	public sealed class InnerJoinValues<TValueWrapper, TOn> : JoinBase<TValueWrapper, TOn, BqlNone>
	where TValueWrapper : IBqlTable, IValuesMapping
	where TOn : class, IBqlOn, new()
	{

		#region Method Overrides

		#region getJoinType

		public override YaqlJoinType getJoinType() => throw new NotImplementedException();

		#endregion

		#endregion

		#region Constructor

		public InnerJoinValues() => throw new NotImplementedException();

		#endregion

	}
}