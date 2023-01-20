using PX.Data;
using PX.Data.BQL;
using PX.Objects.AP;
using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace JoinValuesExperiment
{
	public class EXTestProc : PXGraph<EXTestProc>
	{

		#region Nested Types

		#region APInvoiceValuesMapping

		public class APInvoiceValuesMapping : IBqlTable, IValuesMapping
		{

			#region DocType
			public abstract class docType : BqlString.Field<docType>
			{
			}
			#endregion

			#region RefNbr
			public abstract class refNbr : BqlString.Field<refNbr>
			{
			}
			#endregion

		}

		#endregion

		#endregion

		#region Actions

		// Has to go after Views since for some reason not doing so makes the Cancel button appear last.
		public PXCancel<EXTestProcFilter> Cancel;

		#endregion

		#region Views

		public PXSetup<APSetup> APSetup;

		public PXFilter<EXTestProcFilter> Filter;

		public PXFilteredProcessing<APInvoice, EXTestProcFilter> Records;

		#endregion

		#region Methods

		#region makeGenericType

		private static Type makeGenericType(List<Type> types)
		{
			int length = types.Count;

			if (length == 0)
			{
				throw new ArgumentException("Length of {0} is 0.", nameof(types));
			}

			int index = length - 1;
			int numberOfGenericArgs;
			int skippedTypes = 0;
			Type[] genericArgsArray;

			/*
			 * Example :
			 * Where2<,>
			 * Where<APTran.tranType, Equal<Required<APTran.tranType>>>
			 * Or<>
			 * Where<APTran.refNbr, Equal<Required<APTran.refNbr>>>
			 */

			while (index >= 0)
			{
				if (types[index].IsGenericTypeDefinition)
				{
					numberOfGenericArgs = types[index].GetGenericArguments().Length;

					if (numberOfGenericArgs > skippedTypes)
					{
						throw new Exception($"Not enough Type instances in {nameof(types)} to compose {types[index].Name}.");
					}

					genericArgsArray = new Type[numberOfGenericArgs];

					for (int i_GenericArgs = 0; i_GenericArgs < numberOfGenericArgs; ++i_GenericArgs)
					{
						genericArgsArray[i_GenericArgs] = types[index + i_GenericArgs + 1];
					}

					types[index] = types[index].MakeGenericType(genericArgsArray);
					/*
					 * RemoveRange does a bunch of array copying, which isn't really necessary.
					 * Rather than doing that, we just keep track of how many skipped indexes there are,
					 * which will allow us to determine if there are enough Types in the list for the generic arguments.
					 * The current type we've constructed should count as 1, which is why we're not resetting to 0.
					 */
					skippedTypes = 1;
				}
				else
				{
					++skippedTypes;
				}

				--index;
			}

			return types[0];
		}

		#endregion

		#region testWhereChain

		private void testWhereChain(List<APInvoice> apInvoices, int length, bool recursive)
		{
			Stopwatch timer = Stopwatch.StartNew();

			PXSelectBase<APTran> cmd = new PXSelectReadonly<APTran>(this);
			List<object> args = new List<object>(length * 2);
			List<Type> bql = new List<Type>()
			{
				typeof(Where<>)
			};
			--length;

			for (int i_APInvoices = 0; i_APInvoices < length; ++i_APInvoices)
			{
				bql.Add(typeof(Where2<,>));
				bql.Add
				(
					typeof
					(
						Where
						<
							APTran.tranType, Equal<Required<APTran.tranType>>,
							And<APTran.refNbr, Equal<Required<APTran.refNbr>>>
						>
					)
				);
				bql.Add(typeof(Or<>));

				args.Add(apInvoices[i_APInvoices].DocType);
				args.Add(apInvoices[i_APInvoices].RefNbr);
			}

			bql.Add
			(
				typeof
				(
					Where
					<
						APTran.tranType, Equal<Required<APTran.tranType>>,
						And<APTran.refNbr, Equal<Required<APTran.refNbr>>>
					>
				)
			);
			args.Add(apInvoices[length].DocType);
			args.Add(apInvoices[length].RefNbr);

			cmd.WhereAnd(recursive ? BqlCommand.Compose(bql.ToArray()) : makeGenericType(bql));

			List<APTran> apTrans = new List<APTran>();

			foreach (APTran apTran in cmd.Select(args.ToArray()))
			{
				// Arbitrary work.
				apTrans.Add(apTran);
			}

			timer.Stop();

			PXTrace.WriteInformation($"Ticks for WhereChain where Recursive is {recursive} : {timer.ElapsedTicks}");
		}

		#endregion

		#region testValues

		private void testValues(List<APInvoice> apInvoices, int length)
		{
			Stopwatch timer = Stopwatch.StartNew();

			object[] args = new object[length * 2];

			for (int i_APInvoices = 0; i_APInvoices < length; ++i_APInvoices)
			{
				args[i_APInvoices * 2] = apInvoices[length].DocType;
				args[(i_APInvoices * 2) + 1] = apInvoices[length].RefNbr;
			}

			List<APTran> apTrans = new List<APTran>();

			foreach
			(
				APTran apTran in PXSelectReadonly2
				<
					APTran,
					InnerJoinValues
					<
						APInvoiceValuesMapping,
						On
						<
							APInvoiceValuesMapping.docType, Equal<APTran.tranType>,
							And<APInvoiceValuesMapping.refNbr, Equal<APTran.refNbr>>
						>
					>
				>.Select(this, new object[] { args })
			)
			{
				// Arbitrary work.
				apTrans.Add(apTran);
			}

			timer.Stop();

			PXTrace.WriteInformation($"Ticks for Values : {timer.ElapsedTicks}");
		}

		#endregion

		#region process

		private static void process(List<APInvoice> apInvoices, string processingMethod)
		{
			int length = apInvoices.Count;

			if (length == 0)
			{
				return;
			}

			EXTestProc exTestProc = PXGraph.CreateInstance<EXTestProc>();

			switch (processingMethod)
			{
				case EXTestProcMethods.WhereChainRecursive:
					exTestProc.testWhereChain(apInvoices, length, true);
					break;
				case EXTestProcMethods.WhereChainNonRecursive:
					exTestProc.testWhereChain(apInvoices, length, false);
					break;
				case EXTestProcMethods.Values:
					exTestProc.testValues(apInvoices, length);
					break;
			}
		}

		#endregion

		#endregion

		#region Events

		#region EXTestProcFilter

		protected virtual void __(Events.RowSelected<EXTestProcFilter> e)
		{
			if (e.Row is EXTestProcFilter row)
			{
				Records.SetProcessDelegate(list => process(list, row.ProcessingMethod));
			}
		}

		#endregion

		#endregion

		#region Constructor

		public EXTestProc()
		{
			_ = APSetup.Current;

			// String value is protected within PXProcessing, or else I'd use it rather than risk misspelling it / issues should Acumatica change the implementation.
			Actions["Schedule"]?.SetVisible(false);
		}

		#endregion

	}
}