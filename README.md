# Purpose of this repository #

This repository contains sample code to test an issue I would like Acumatica to address with a feature I'm proposing on the [Community Forum](https://community.acumatica.com/ideas/add-bql-functionality-to-allow-queries-commands-using-values-from-sql-to-improve-performance-13887).
Also in this repository is a mock-up of how I'd imagine the implementation would look like, as well as some steps for how to generate ample data for testing and
some testing steps.

Some of the information in this file will be duplicate from the forum post, but will have nicer formatting :wink:

# The problem #

In some cases, we have a set of data keys that was either provided by the user, or related to data provided by the user and collected in the back-end.
Imagine something like a processing screen where a user pressed "Process All" or perhaps a screen such as APPaymentEntry, and the Adjustments grid has a large number
of APAdjusts.

For example, say that we have N DocType, RefNbr value pairs for APInvoices, and N is very large.

In some cases we can use BqlCommand.Compose to construct a Type for something like this :

```cs
List<Type> types = new List<Type>()
{
	typeof(Where2<,>)
};
```

Then append many occurrences of

```cs
Or
<
	Where
	<
		APInvoice.docType, Equal<Required<APInvoice.docType>>,
		And<APInvoice.refNbr, Equal<Required<APInvoice.refNbr>>>
	>
>
```

In order to generate a query such as

```sql
SELECT
	*
FROM
(
	SELECT
		....
	FROM APRegister R
	INNER JOIN APInvoice I ON
		I.CompanyID = R.CompanyID
		AND I.DocType = R.DocType
		AND I.RefNbr = R.RefNbr
) [APInvoice]
WHERE ComapnyID = 2
AND
(

	(DocType = ... AND RefNbr = ...)
	OR (DocType = ... AND RefNbr = ...)
	....
)
```

However, doing so is rather tedious, and Acumatica imposed a recursion limit that you need access to the WebConfig to change.
It's also not possible to compose PXUpdates like this due to Acumatica not making much of its code publicly available.

Implementing the BqlCommand.Compose logic myself in a client project showed that when N is rather large (but not too large), doing all these ORs wasn't efficient.

However, for cases where the Select is a plain Select, making N calls to the database could be worse, and for cases where an Aggregate is necessary, there doesn't seem to be another option.

While it may seem like you could do something like this :

```sql
AND APInvoice.DocType IN (...)
AND APInvoice.RefNbr IN (...)
```

This makes it possible to select data you don't want, in the case where given the following existing APInvoices :
```
ADR 1234
INV 1234
ADR 2222
```

If you only want `ADR 2222` and `INV 1234`, IN would return `ADR 1234`.

This is a rather simple use case to make understanding the concept easy, so I'm not looking for alternative solutions that only apply to the simple use case.

# The proposal #

Looking into possible ways to speed up such queries, I found that some people suggested creating a temporary table to join to.
This is plausible in normal queries, but within Acumatica this isn't possible within Standard, and would be tricky to do safely/properly anyways.

I found that it's actually possible to join to VALUES similar to how you would write an INSERT command for hard-coded values.
It looks like this :

```sql
SELECT
	*
FROM
(
	SELECT
		....
	FROM APRegister R
	INNER JOIN APInvoice I ON
		I.CompanyID = R.CompanyID
		AND I.DocType = R.DocType
		AND I.RefNbr = R.RefNbr
) [APInvoice]
INNER JOIN
(
	VALUES
		('ADR', '1234'),
		('INV', '1222'),
		...
) KnownKeys(DocType, RefNbr) ON
	KnownKeys.DocType = APInvoice.DocType
	AND KnownKeys.RefNbr = APInvoice.RefNbr
WHERE APInvoice.CompanyID = 2
```

Within Acumatica I would imagine an implementation like this :

Given an interface `IValuesMapping`

```cs
public interface IValuesMapping
{
}
```

We can write mappings, that look like DACs, but have no properties :

```cs
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
```

We can then write queries like so :

```cs
PXSelectReadonly2
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
>
```

Way easier than writing the alternative BQL using BqlCommand.Compose.

# How to run the code #

1) Pull the repository.
2) Delete the JoinValuesExperiment_22_207_0013 folder. This will prevent an issue that happens when creating a website in a non-empty directory.
3) Create an Acumatica instance with SalesDemo data for the JoinValuesExperiment_22_207_0013 folder using 22.207.0013.
4) Undo the changes.
5) Get rid of the 3 default Customization Packages, and publish the provided zip.
6) Rebuild just in case.
7) In the WebConfig, set the MaxRecursionLevel to something ridiculous : `<add key="MaxRecursionLevel" value="100000"/>`
8) Go to EX501000, pick a method, and hit Process All.

Rather, that's what the instructions would be in an ideal world.
"WhereChain Recursive" uses BqlCommand.Compose from base Acumatica, while "WhereChain NonRecursive" uses a method I wrote to attempt to replicate Acumatica's
functionality without the stack overflow exception I was getting. I say "attempt" because I couldn't find a way to prevent the exception and am not sure why it's
even happening. I've processed orders of magnitude more data than the ~2k records, with way more instructions than are in this method and never got stack overflows.

If the stack overflow problem can be resolved, then this code would compare the times of the 2 WhereChain methods to the proposed JoinValues functionality.

If Acumatica implements the proposed functionality exactly like how I've written InnerJoinValues, then my InnerJoinValues can be deleted, as can the interface,
then the code should compile and run. Otherwise, alterations will have to be made.

# Analyzing the queries, and further thoughts #

Since I couldn't get Acumatica to work with either version of the code, I've generated the two queries myself. They're a bit different from what Acumatica
would generate, as they use `sp_executesql`, optimization hints, and LOTS of square brackets.

As far as I can tell, even for 2,000 Invoices, either query is fast, BUT it appears to me that the VALUES query is faster.
If this proves to be true over more thorough testing, then VALUES should definitely be implemented within Acumatica.

This SQL functionality can be extended to other areas, such as Updates, and Inserts. Although inserting using VALUES has a limit of 1,000 rows unless you
turn it into a SELECT FROM. I believe I'd tested this at one point, and found that it performed poorly compared to SqlBulkInsert, however, we're **definitely**
not allowed to use that within Acumatica Standard, which I try to stick to.

Being able to bulk insert and bulk update records using VALUES would be a big + for performance. Although, it'd have to be used carefully, as there are some
cases where it would likely cause issues with Acumatica functionality. I don't think this would play nicely with Accumulative tables, although I may be mistaken
in my understanding of how they're implemented, it's been a few years since I've messed around with them.

Below are the two queries.

And/Or
```sql
SELECT
	*
FROM APTran
WHERE CompanyID = 2
AND
(
	(TranType = 'ACR' AND RefNbr = '000417')
	OR (TranType = 'ADR' AND RefNbr = '000400')
	OR (TranType = 'ADR' AND RefNbr = '000418')
	OR (TranType = 'ADR' AND RefNbr = '000419')
	OR (TranType = 'ADR' AND RefNbr = '001261')
	OR (TranType = 'ADR' AND RefNbr = '001262')
	OR (TranType = 'ADR' AND RefNbr = '001577')
	OR (TranType = 'ADR' AND RefNbr = '001578')
	OR (TranType = 'ADR' AND RefNbr = '002001')
	OR (TranType = 'ADR' AND RefNbr = '002043')
	OR (TranType = 'ADR' AND RefNbr = '002455')
	OR (TranType = 'ADR' AND RefNbr = '002456')
	OR (TranType = 'ADR' AND RefNbr = '002457')
	OR (TranType = 'INV' AND RefNbr = '000183')
	OR (TranType = 'INV' AND RefNbr = '000184')
	OR (TranType = 'INV' AND RefNbr = '000185')
	OR (TranType = 'INV' AND RefNbr = '000186')
	OR (TranType = 'INV' AND RefNbr = '000187')
	OR (TranType = 'INV' AND RefNbr = '000188')
	OR (TranType = 'INV' AND RefNbr = '000189')
	OR (TranType = 'INV' AND RefNbr = '000190')
	OR (TranType = 'INV' AND RefNbr = '000191')
	OR (TranType = 'INV' AND RefNbr = '000192')
	OR (TranType = 'INV' AND RefNbr = '000193')
	OR (TranType = 'INV' AND RefNbr = '000194')
	OR (TranType = 'INV' AND RefNbr = '000195')
	OR (TranType = 'INV' AND RefNbr = '000196')
	OR (TranType = 'INV' AND RefNbr = '000197')
	OR (TranType = 'INV' AND RefNbr = '000198')
	OR (TranType = 'INV' AND RefNbr = '000199')
	OR (TranType = 'INV' AND RefNbr = '000200')
	OR (TranType = 'INV' AND RefNbr = '000201')
	OR (TranType = 'INV' AND RefNbr = '000202')
	OR (TranType = 'INV' AND RefNbr = '000203')
	OR (TranType = 'INV' AND RefNbr = '000204')
	OR (TranType = 'INV' AND RefNbr = '000205')
	OR (TranType = 'INV' AND RefNbr = '000206')
	OR (TranType = 'INV' AND RefNbr = '000207')
	OR (TranType = 'INV' AND RefNbr = '000208')
	OR (TranType = 'INV' AND RefNbr = '000209')
	OR (TranType = 'INV' AND RefNbr = '000210')
	OR (TranType = 'INV' AND RefNbr = '000211')
	OR (TranType = 'INV' AND RefNbr = '000212')
	OR (TranType = 'INV' AND RefNbr = '000213')
	OR (TranType = 'INV' AND RefNbr = '000214')
	OR (TranType = 'INV' AND RefNbr = '000215')
	OR (TranType = 'INV' AND RefNbr = '000216')
	OR (TranType = 'INV' AND RefNbr = '000217')
	OR (TranType = 'INV' AND RefNbr = '000218')
	OR (TranType = 'INV' AND RefNbr = '000219')
	OR (TranType = 'INV' AND RefNbr = '000220')
	OR (TranType = 'INV' AND RefNbr = '000221')
	OR (TranType = 'INV' AND RefNbr = '000222')
	OR (TranType = 'INV' AND RefNbr = '000223')
	OR (TranType = 'INV' AND RefNbr = '000224')
	OR (TranType = 'INV' AND RefNbr = '000225')
	OR (TranType = 'INV' AND RefNbr = '000226')
	OR (TranType = 'INV' AND RefNbr = '000227')
	OR (TranType = 'INV' AND RefNbr = '000228')
	OR (TranType = 'INV' AND RefNbr = '000229')
	OR (TranType = 'INV' AND RefNbr = '000230')
	OR (TranType = 'INV' AND RefNbr = '000231')
	OR (TranType = 'INV' AND RefNbr = '000232')
	OR (TranType = 'INV' AND RefNbr = '000233')
	OR (TranType = 'INV' AND RefNbr = '000234')
	OR (TranType = 'INV' AND RefNbr = '000235')
	OR (TranType = 'INV' AND RefNbr = '000236')
	OR (TranType = 'INV' AND RefNbr = '000237')
	OR (TranType = 'INV' AND RefNbr = '000238')
	OR (TranType = 'INV' AND RefNbr = '000239')
	OR (TranType = 'INV' AND RefNbr = '000240')
	OR (TranType = 'INV' AND RefNbr = '000241')
	OR (TranType = 'INV' AND RefNbr = '000242')
	OR (TranType = 'INV' AND RefNbr = '000243')
	OR (TranType = 'INV' AND RefNbr = '000244')
	OR (TranType = 'INV' AND RefNbr = '000245')
	OR (TranType = 'INV' AND RefNbr = '000246')
	OR (TranType = 'INV' AND RefNbr = '000247')
	OR (TranType = 'INV' AND RefNbr = '000248')
	OR (TranType = 'INV' AND RefNbr = '000249')
	OR (TranType = 'INV' AND RefNbr = '000250')
	OR (TranType = 'INV' AND RefNbr = '000251')
	OR (TranType = 'INV' AND RefNbr = '000252')
	OR (TranType = 'INV' AND RefNbr = '000253')
	OR (TranType = 'INV' AND RefNbr = '000254')
	OR (TranType = 'INV' AND RefNbr = '000255')
	OR (TranType = 'INV' AND RefNbr = '000256')
	OR (TranType = 'INV' AND RefNbr = '000257')
	OR (TranType = 'INV' AND RefNbr = '000258')
	OR (TranType = 'INV' AND RefNbr = '000259')
	OR (TranType = 'INV' AND RefNbr = '000260')
	OR (TranType = 'INV' AND RefNbr = '000261')
	OR (TranType = 'INV' AND RefNbr = '000262')
	OR (TranType = 'INV' AND RefNbr = '000263')
	OR (TranType = 'INV' AND RefNbr = '000264')
	OR (TranType = 'INV' AND RefNbr = '000265')
	OR (TranType = 'INV' AND RefNbr = '000266')
	OR (TranType = 'INV' AND RefNbr = '000267')
	OR (TranType = 'INV' AND RefNbr = '000268')
	OR (TranType = 'INV' AND RefNbr = '000269')
	OR (TranType = 'INV' AND RefNbr = '000270')
	OR (TranType = 'INV' AND RefNbr = '000271')
	OR (TranType = 'INV' AND RefNbr = '000272')
	OR (TranType = 'INV' AND RefNbr = '000273')
	OR (TranType = 'INV' AND RefNbr = '000274')
	OR (TranType = 'INV' AND RefNbr = '000275')
	OR (TranType = 'INV' AND RefNbr = '000276')
	OR (TranType = 'INV' AND RefNbr = '000277')
	OR (TranType = 'INV' AND RefNbr = '000278')
	OR (TranType = 'INV' AND RefNbr = '000279')
	OR (TranType = 'INV' AND RefNbr = '000280')
	OR (TranType = 'INV' AND RefNbr = '000281')
	OR (TranType = 'INV' AND RefNbr = '000282')
	OR (TranType = 'INV' AND RefNbr = '000283')
	OR (TranType = 'INV' AND RefNbr = '000284')
	OR (TranType = 'INV' AND RefNbr = '000285')
	OR (TranType = 'INV' AND RefNbr = '000286')
	OR (TranType = 'INV' AND RefNbr = '000287')
	OR (TranType = 'INV' AND RefNbr = '000288')
	OR (TranType = 'INV' AND RefNbr = '000289')
	OR (TranType = 'INV' AND RefNbr = '000290')
	OR (TranType = 'INV' AND RefNbr = '000291')
	OR (TranType = 'INV' AND RefNbr = '000292')
	OR (TranType = 'INV' AND RefNbr = '000293')
	OR (TranType = 'INV' AND RefNbr = '000294')
	OR (TranType = 'INV' AND RefNbr = '000295')
	OR (TranType = 'INV' AND RefNbr = '000296')
	OR (TranType = 'INV' AND RefNbr = '000297')
	OR (TranType = 'INV' AND RefNbr = '000298')
	OR (TranType = 'INV' AND RefNbr = '000299')
	OR (TranType = 'INV' AND RefNbr = '000300')
	OR (TranType = 'INV' AND RefNbr = '000301')
	OR (TranType = 'INV' AND RefNbr = '000302')
	OR (TranType = 'INV' AND RefNbr = '000303')
	OR (TranType = 'INV' AND RefNbr = '000304')
	OR (TranType = 'INV' AND RefNbr = '000305')
	OR (TranType = 'INV' AND RefNbr = '000306')
	OR (TranType = 'INV' AND RefNbr = '000307')
	OR (TranType = 'INV' AND RefNbr = '000308')
	OR (TranType = 'INV' AND RefNbr = '000309')
	OR (TranType = 'INV' AND RefNbr = '000310')
	OR (TranType = 'INV' AND RefNbr = '000311')
	OR (TranType = 'INV' AND RefNbr = '000312')
	OR (TranType = 'INV' AND RefNbr = '000313')
	OR (TranType = 'INV' AND RefNbr = '000314')
	OR (TranType = 'INV' AND RefNbr = '000315')
	OR (TranType = 'INV' AND RefNbr = '000316')
	OR (TranType = 'INV' AND RefNbr = '000317')
	OR (TranType = 'INV' AND RefNbr = '000318')
	OR (TranType = 'INV' AND RefNbr = '000319')
	OR (TranType = 'INV' AND RefNbr = '000320')
	OR (TranType = 'INV' AND RefNbr = '000321')
	OR (TranType = 'INV' AND RefNbr = '000322')
	OR (TranType = 'INV' AND RefNbr = '000323')
	OR (TranType = 'INV' AND RefNbr = '000324')
	OR (TranType = 'INV' AND RefNbr = '000325')
	OR (TranType = 'INV' AND RefNbr = '000326')
	OR (TranType = 'INV' AND RefNbr = '000327')
	OR (TranType = 'INV' AND RefNbr = '000328')
	OR (TranType = 'INV' AND RefNbr = '000329')
	OR (TranType = 'INV' AND RefNbr = '000330')
	OR (TranType = 'INV' AND RefNbr = '000331')
	OR (TranType = 'INV' AND RefNbr = '000332')
	OR (TranType = 'INV' AND RefNbr = '000333')
	OR (TranType = 'INV' AND RefNbr = '000334')
	OR (TranType = 'INV' AND RefNbr = '000335')
	OR (TranType = 'INV' AND RefNbr = '000336')
	OR (TranType = 'INV' AND RefNbr = '000337')
	OR (TranType = 'INV' AND RefNbr = '000338')
	OR (TranType = 'INV' AND RefNbr = '000339')
	OR (TranType = 'INV' AND RefNbr = '000341')
	OR (TranType = 'INV' AND RefNbr = '000342')
	OR (TranType = 'INV' AND RefNbr = '000343')
	OR (TranType = 'INV' AND RefNbr = '000344')
	OR (TranType = 'INV' AND RefNbr = '000345')
	OR (TranType = 'INV' AND RefNbr = '000346')
	OR (TranType = 'INV' AND RefNbr = '000347')
	OR (TranType = 'INV' AND RefNbr = '000348')
	OR (TranType = 'INV' AND RefNbr = '000349')
	OR (TranType = 'INV' AND RefNbr = '000350')
	OR (TranType = 'INV' AND RefNbr = '000351')
	OR (TranType = 'INV' AND RefNbr = '000352')
	OR (TranType = 'INV' AND RefNbr = '000353')
	OR (TranType = 'INV' AND RefNbr = '000354')
	OR (TranType = 'INV' AND RefNbr = '000355')
	OR (TranType = 'INV' AND RefNbr = '000356')
	OR (TranType = 'INV' AND RefNbr = '000357')
	OR (TranType = 'INV' AND RefNbr = '000358')
	OR (TranType = 'INV' AND RefNbr = '000359')
	OR (TranType = 'INV' AND RefNbr = '000360')
	OR (TranType = 'INV' AND RefNbr = '000361')
	OR (TranType = 'INV' AND RefNbr = '000362')
	OR (TranType = 'INV' AND RefNbr = '000363')
	OR (TranType = 'INV' AND RefNbr = '000364')
	OR (TranType = 'INV' AND RefNbr = '000365')
	OR (TranType = 'INV' AND RefNbr = '000366')
	OR (TranType = 'INV' AND RefNbr = '000367')
	OR (TranType = 'INV' AND RefNbr = '000368')
	OR (TranType = 'INV' AND RefNbr = '000369')
	OR (TranType = 'INV' AND RefNbr = '000370')
	OR (TranType = 'INV' AND RefNbr = '000371')
	OR (TranType = 'INV' AND RefNbr = '000372')
	OR (TranType = 'INV' AND RefNbr = '000373')
	OR (TranType = 'INV' AND RefNbr = '000374')
	OR (TranType = 'INV' AND RefNbr = '000375')
	OR (TranType = 'INV' AND RefNbr = '000376')
	OR (TranType = 'INV' AND RefNbr = '000377')
	OR (TranType = 'INV' AND RefNbr = '000378')
	OR (TranType = 'INV' AND RefNbr = '000379')
	OR (TranType = 'INV' AND RefNbr = '000380')
	OR (TranType = 'INV' AND RefNbr = '000381')
	OR (TranType = 'INV' AND RefNbr = '000382')
	OR (TranType = 'INV' AND RefNbr = '000383')
	OR (TranType = 'INV' AND RefNbr = '000384')
	OR (TranType = 'INV' AND RefNbr = '000385')
	OR (TranType = 'INV' AND RefNbr = '000386')
	OR (TranType = 'INV' AND RefNbr = '000387')
	OR (TranType = 'INV' AND RefNbr = '000388')
	OR (TranType = 'INV' AND RefNbr = '000389')
	OR (TranType = 'INV' AND RefNbr = '000390')
	OR (TranType = 'INV' AND RefNbr = '000391')
	OR (TranType = 'INV' AND RefNbr = '000392')
	OR (TranType = 'INV' AND RefNbr = '000393')
	OR (TranType = 'INV' AND RefNbr = '000394')
	OR (TranType = 'INV' AND RefNbr = '000395')
	OR (TranType = 'INV' AND RefNbr = '000396')
	OR (TranType = 'INV' AND RefNbr = '000397')
	OR (TranType = 'INV' AND RefNbr = '000398')
	OR (TranType = 'INV' AND RefNbr = '000399')
	OR (TranType = 'INV' AND RefNbr = '000401')
	OR (TranType = 'INV' AND RefNbr = '000402')
	OR (TranType = 'INV' AND RefNbr = '000403')
	OR (TranType = 'INV' AND RefNbr = '000404')
	OR (TranType = 'INV' AND RefNbr = '000405')
	OR (TranType = 'INV' AND RefNbr = '000406')
	OR (TranType = 'INV' AND RefNbr = '000407')
	OR (TranType = 'INV' AND RefNbr = '000408')
	OR (TranType = 'INV' AND RefNbr = '000409')
	OR (TranType = 'INV' AND RefNbr = '000410')
	OR (TranType = 'INV' AND RefNbr = '000411')
	OR (TranType = 'INV' AND RefNbr = '000412')
	OR (TranType = 'INV' AND RefNbr = '000413')
	OR (TranType = 'INV' AND RefNbr = '000414')
	OR (TranType = 'INV' AND RefNbr = '000415')
	OR (TranType = 'INV' AND RefNbr = '000416')
	OR (TranType = 'INV' AND RefNbr = '000420')
	OR (TranType = 'INV' AND RefNbr = '000421')
	OR (TranType = 'INV' AND RefNbr = '000422')
	OR (TranType = 'INV' AND RefNbr = '000423')
	OR (TranType = 'INV' AND RefNbr = '000424')
	OR (TranType = 'INV' AND RefNbr = '000425')
	OR (TranType = 'INV' AND RefNbr = '000426')
	OR (TranType = 'INV' AND RefNbr = '000427')
	OR (TranType = 'INV' AND RefNbr = '000428')
	OR (TranType = 'INV' AND RefNbr = '000429')
	OR (TranType = 'INV' AND RefNbr = '000430')
	OR (TranType = 'INV' AND RefNbr = '000431')
	OR (TranType = 'INV' AND RefNbr = '000432')
	OR (TranType = 'INV' AND RefNbr = '000433')
	OR (TranType = 'INV' AND RefNbr = '000434')
	OR (TranType = 'INV' AND RefNbr = '000435')
	OR (TranType = 'INV' AND RefNbr = '000436')
	OR (TranType = 'INV' AND RefNbr = '000437')
	OR (TranType = 'INV' AND RefNbr = '000438')
	OR (TranType = 'INV' AND RefNbr = '000439')
	OR (TranType = 'INV' AND RefNbr = '000440')
	OR (TranType = 'INV' AND RefNbr = '000441')
	OR (TranType = 'INV' AND RefNbr = '000442')
	OR (TranType = 'INV' AND RefNbr = '000443')
	OR (TranType = 'INV' AND RefNbr = '000444')
	OR (TranType = 'INV' AND RefNbr = '000446')
	OR (TranType = 'INV' AND RefNbr = '000447')
	OR (TranType = 'INV' AND RefNbr = '000448')
	OR (TranType = 'INV' AND RefNbr = '000449')
	OR (TranType = 'INV' AND RefNbr = '000450')
	OR (TranType = 'INV' AND RefNbr = '000451')
	OR (TranType = 'INV' AND RefNbr = '000452')
	OR (TranType = 'INV' AND RefNbr = '000453')
	OR (TranType = 'INV' AND RefNbr = '000454')
	OR (TranType = 'INV' AND RefNbr = '000455')
	OR (TranType = 'INV' AND RefNbr = '000456')
	OR (TranType = 'INV' AND RefNbr = '000457')
	OR (TranType = 'INV' AND RefNbr = '000458')
	OR (TranType = 'INV' AND RefNbr = '000459')
	OR (TranType = 'INV' AND RefNbr = '000460')
	OR (TranType = 'INV' AND RefNbr = '000461')
	OR (TranType = 'INV' AND RefNbr = '000462')
	OR (TranType = 'INV' AND RefNbr = '000463')
	OR (TranType = 'INV' AND RefNbr = '000464')
	OR (TranType = 'INV' AND RefNbr = '000465')
	OR (TranType = 'INV' AND RefNbr = '000466')
	OR (TranType = 'INV' AND RefNbr = '000467')
	OR (TranType = 'INV' AND RefNbr = '000468')
	OR (TranType = 'INV' AND RefNbr = '000469')
	OR (TranType = 'INV' AND RefNbr = '000470')
	OR (TranType = 'INV' AND RefNbr = '000471')
	OR (TranType = 'INV' AND RefNbr = '000472')
	OR (TranType = 'INV' AND RefNbr = '000473')
	OR (TranType = 'INV' AND RefNbr = '000474')
	OR (TranType = 'INV' AND RefNbr = '000475')
	OR (TranType = 'INV' AND RefNbr = '000476')
	OR (TranType = 'INV' AND RefNbr = '000477')
	OR (TranType = 'INV' AND RefNbr = '000478')
	OR (TranType = 'INV' AND RefNbr = '000479')
	OR (TranType = 'INV' AND RefNbr = '000480')
	OR (TranType = 'INV' AND RefNbr = '000481')
	OR (TranType = 'INV' AND RefNbr = '000482')
	OR (TranType = 'INV' AND RefNbr = '000483')
	OR (TranType = 'INV' AND RefNbr = '000484')
	OR (TranType = 'INV' AND RefNbr = '000485')
	OR (TranType = 'INV' AND RefNbr = '000486')
	OR (TranType = 'INV' AND RefNbr = '000487')
	OR (TranType = 'INV' AND RefNbr = '000488')
	OR (TranType = 'INV' AND RefNbr = '000489')
	OR (TranType = 'INV' AND RefNbr = '000490')
	OR (TranType = 'INV' AND RefNbr = '000491')
	OR (TranType = 'INV' AND RefNbr = '000492')
	OR (TranType = 'INV' AND RefNbr = '000493')
	OR (TranType = 'INV' AND RefNbr = '000494')
	OR (TranType = 'INV' AND RefNbr = '000495')
	OR (TranType = 'INV' AND RefNbr = '000496')
	OR (TranType = 'INV' AND RefNbr = '000497')
	OR (TranType = 'INV' AND RefNbr = '000498')
	OR (TranType = 'INV' AND RefNbr = '000499')
	OR (TranType = 'INV' AND RefNbr = '000500')
	OR (TranType = 'INV' AND RefNbr = '000501')
	OR (TranType = 'INV' AND RefNbr = '000502')
	OR (TranType = 'INV' AND RefNbr = '000503')
	OR (TranType = 'INV' AND RefNbr = '000504')
	OR (TranType = 'INV' AND RefNbr = '000505')
	OR (TranType = 'INV' AND RefNbr = '000506')
	OR (TranType = 'INV' AND RefNbr = '000507')
	OR (TranType = 'INV' AND RefNbr = '000508')
	OR (TranType = 'INV' AND RefNbr = '000509')
	OR (TranType = 'INV' AND RefNbr = '000510')
	OR (TranType = 'INV' AND RefNbr = '000511')
	OR (TranType = 'INV' AND RefNbr = '000512')
	OR (TranType = 'INV' AND RefNbr = '000513')
	OR (TranType = 'INV' AND RefNbr = '000514')
	OR (TranType = 'INV' AND RefNbr = '000515')
	OR (TranType = 'INV' AND RefNbr = '000516')
	OR (TranType = 'INV' AND RefNbr = '000517')
	OR (TranType = 'INV' AND RefNbr = '000518')
	OR (TranType = 'INV' AND RefNbr = '000519')
	OR (TranType = 'INV' AND RefNbr = '000520')
	OR (TranType = 'INV' AND RefNbr = '000521')
	OR (TranType = 'INV' AND RefNbr = '000522')
	OR (TranType = 'INV' AND RefNbr = '000523')
	OR (TranType = 'INV' AND RefNbr = '000524')
	OR (TranType = 'INV' AND RefNbr = '000525')
	OR (TranType = 'INV' AND RefNbr = '000526')
	OR (TranType = 'INV' AND RefNbr = '000527')
	OR (TranType = 'INV' AND RefNbr = '000528')
	OR (TranType = 'INV' AND RefNbr = '000529')
	OR (TranType = 'INV' AND RefNbr = '000530')
	OR (TranType = 'INV' AND RefNbr = '000531')
	OR (TranType = 'INV' AND RefNbr = '000532')
	OR (TranType = 'INV' AND RefNbr = '000533')
	OR (TranType = 'INV' AND RefNbr = '000534')
	OR (TranType = 'INV' AND RefNbr = '000535')
	OR (TranType = 'INV' AND RefNbr = '000536')
	OR (TranType = 'INV' AND RefNbr = '000537')
	OR (TranType = 'INV' AND RefNbr = '000538')
	OR (TranType = 'INV' AND RefNbr = '000539')
	OR (TranType = 'INV' AND RefNbr = '000540')
	OR (TranType = 'INV' AND RefNbr = '000541')
	OR (TranType = 'INV' AND RefNbr = '000542')
	OR (TranType = 'INV' AND RefNbr = '000543')
	OR (TranType = 'INV' AND RefNbr = '000544')
	OR (TranType = 'INV' AND RefNbr = '000545')
	OR (TranType = 'INV' AND RefNbr = '000546')
	OR (TranType = 'INV' AND RefNbr = '000547')
	OR (TranType = 'INV' AND RefNbr = '000548')
	OR (TranType = 'INV' AND RefNbr = '000549')
	OR (TranType = 'INV' AND RefNbr = '000550')
	OR (TranType = 'INV' AND RefNbr = '000551')
	OR (TranType = 'INV' AND RefNbr = '000552')
	OR (TranType = 'INV' AND RefNbr = '000553')
	OR (TranType = 'INV' AND RefNbr = '000554')
	OR (TranType = 'INV' AND RefNbr = '000555')
	OR (TranType = 'INV' AND RefNbr = '000556')
	OR (TranType = 'INV' AND RefNbr = '000557')
	OR (TranType = 'INV' AND RefNbr = '000558')
	OR (TranType = 'INV' AND RefNbr = '000559')
	OR (TranType = 'INV' AND RefNbr = '000560')
	OR (TranType = 'INV' AND RefNbr = '000561')
	OR (TranType = 'INV' AND RefNbr = '000562')
	OR (TranType = 'INV' AND RefNbr = '000563')
	OR (TranType = 'INV' AND RefNbr = '000564')
	OR (TranType = 'INV' AND RefNbr = '000565')
	OR (TranType = 'INV' AND RefNbr = '000566')
	OR (TranType = 'INV' AND RefNbr = '000567')
	OR (TranType = 'INV' AND RefNbr = '000568')
	OR (TranType = 'INV' AND RefNbr = '000569')
	OR (TranType = 'INV' AND RefNbr = '000570')
	OR (TranType = 'INV' AND RefNbr = '000571')
	OR (TranType = 'INV' AND RefNbr = '000572')
	OR (TranType = 'INV' AND RefNbr = '000573')
	OR (TranType = 'INV' AND RefNbr = '000574')
	OR (TranType = 'INV' AND RefNbr = '000575')
	OR (TranType = 'INV' AND RefNbr = '000576')
	OR (TranType = 'INV' AND RefNbr = '000577')
	OR (TranType = 'INV' AND RefNbr = '000578')
	OR (TranType = 'INV' AND RefNbr = '000579')
	OR (TranType = 'INV' AND RefNbr = '000580')
	OR (TranType = 'INV' AND RefNbr = '000581')
	OR (TranType = 'INV' AND RefNbr = '000582')
	OR (TranType = 'INV' AND RefNbr = '000583')
	OR (TranType = 'INV' AND RefNbr = '000584')
	OR (TranType = 'INV' AND RefNbr = '000585')
	OR (TranType = 'INV' AND RefNbr = '000586')
	OR (TranType = 'INV' AND RefNbr = '000587')
	OR (TranType = 'INV' AND RefNbr = '000588')
	OR (TranType = 'INV' AND RefNbr = '000589')
	OR (TranType = 'INV' AND RefNbr = '000590')
	OR (TranType = 'INV' AND RefNbr = '000591')
	OR (TranType = 'INV' AND RefNbr = '000592')
	OR (TranType = 'INV' AND RefNbr = '000593')
	OR (TranType = 'INV' AND RefNbr = '000594')
	OR (TranType = 'INV' AND RefNbr = '000595')
	OR (TranType = 'INV' AND RefNbr = '000596')
	OR (TranType = 'INV' AND RefNbr = '000597')
	OR (TranType = 'INV' AND RefNbr = '000598')
	OR (TranType = 'INV' AND RefNbr = '000599')
	OR (TranType = 'INV' AND RefNbr = '000600')
	OR (TranType = 'INV' AND RefNbr = '000601')
	OR (TranType = 'INV' AND RefNbr = '000602')
	OR (TranType = 'INV' AND RefNbr = '000603')
	OR (TranType = 'INV' AND RefNbr = '000604')
	OR (TranType = 'INV' AND RefNbr = '000605')
	OR (TranType = 'INV' AND RefNbr = '000606')
	OR (TranType = 'INV' AND RefNbr = '000607')
	OR (TranType = 'INV' AND RefNbr = '000608')
	OR (TranType = 'INV' AND RefNbr = '000609')
	OR (TranType = 'INV' AND RefNbr = '000610')
	OR (TranType = 'INV' AND RefNbr = '000611')
	OR (TranType = 'INV' AND RefNbr = '000612')
	OR (TranType = 'INV' AND RefNbr = '000613')
	OR (TranType = 'INV' AND RefNbr = '000614')
	OR (TranType = 'INV' AND RefNbr = '000615')
	OR (TranType = 'INV' AND RefNbr = '000616')
	OR (TranType = 'INV' AND RefNbr = '000617')
	OR (TranType = 'INV' AND RefNbr = '000618')
	OR (TranType = 'INV' AND RefNbr = '000619')
	OR (TranType = 'INV' AND RefNbr = '000620')
	OR (TranType = 'INV' AND RefNbr = '000621')
	OR (TranType = 'INV' AND RefNbr = '000622')
	OR (TranType = 'INV' AND RefNbr = '000623')
	OR (TranType = 'INV' AND RefNbr = '000624')
	OR (TranType = 'INV' AND RefNbr = '000625')
	OR (TranType = 'INV' AND RefNbr = '000626')
	OR (TranType = 'INV' AND RefNbr = '000627')
	OR (TranType = 'INV' AND RefNbr = '000628')
	OR (TranType = 'INV' AND RefNbr = '000629')
	OR (TranType = 'INV' AND RefNbr = '000630')
	OR (TranType = 'INV' AND RefNbr = '000632')
	OR (TranType = 'INV' AND RefNbr = '000633')
	OR (TranType = 'INV' AND RefNbr = '000634')
	OR (TranType = 'INV' AND RefNbr = '000635')
	OR (TranType = 'INV' AND RefNbr = '000636')
	OR (TranType = 'INV' AND RefNbr = '000637')
	OR (TranType = 'INV' AND RefNbr = '000638')
	OR (TranType = 'INV' AND RefNbr = '000639')
	OR (TranType = 'INV' AND RefNbr = '000640')
	OR (TranType = 'INV' AND RefNbr = '000641')
	OR (TranType = 'INV' AND RefNbr = '000642')
	OR (TranType = 'INV' AND RefNbr = '000643')
	OR (TranType = 'INV' AND RefNbr = '000644')
	OR (TranType = 'INV' AND RefNbr = '000645')
	OR (TranType = 'INV' AND RefNbr = '000646')
	OR (TranType = 'INV' AND RefNbr = '000647')
	OR (TranType = 'INV' AND RefNbr = '000648')
	OR (TranType = 'INV' AND RefNbr = '000649')
	OR (TranType = 'INV' AND RefNbr = '000651')
	OR (TranType = 'INV' AND RefNbr = '000652')
	OR (TranType = 'INV' AND RefNbr = '000653')
	OR (TranType = 'INV' AND RefNbr = '000654')
	OR (TranType = 'INV' AND RefNbr = '000655')
	OR (TranType = 'INV' AND RefNbr = '000656')
	OR (TranType = 'INV' AND RefNbr = '000657')
	OR (TranType = 'INV' AND RefNbr = '000658')
	OR (TranType = 'INV' AND RefNbr = '000659')
	OR (TranType = 'INV' AND RefNbr = '000660')
	OR (TranType = 'INV' AND RefNbr = '000661')
	OR (TranType = 'INV' AND RefNbr = '000662')
	OR (TranType = 'INV' AND RefNbr = '000663')
	OR (TranType = 'INV' AND RefNbr = '000664')
	OR (TranType = 'INV' AND RefNbr = '000665')
	OR (TranType = 'INV' AND RefNbr = '000666')
	OR (TranType = 'INV' AND RefNbr = '000667')
	OR (TranType = 'INV' AND RefNbr = '000668')
	OR (TranType = 'INV' AND RefNbr = '000669')
	OR (TranType = 'INV' AND RefNbr = '000670')
	OR (TranType = 'INV' AND RefNbr = '000671')
	OR (TranType = 'INV' AND RefNbr = '000672')
	OR (TranType = 'INV' AND RefNbr = '000673')
	OR (TranType = 'INV' AND RefNbr = '000674')
	OR (TranType = 'INV' AND RefNbr = '000675')
	OR (TranType = 'INV' AND RefNbr = '000676')
	OR (TranType = 'INV' AND RefNbr = '000677')
	OR (TranType = 'INV' AND RefNbr = '000678')
	OR (TranType = 'INV' AND RefNbr = '000679')
	OR (TranType = 'INV' AND RefNbr = '000680')
	OR (TranType = 'INV' AND RefNbr = '000681')
	OR (TranType = 'INV' AND RefNbr = '000682')
	OR (TranType = 'INV' AND RefNbr = '000683')
	OR (TranType = 'INV' AND RefNbr = '000684')
	OR (TranType = 'INV' AND RefNbr = '000685')
	OR (TranType = 'INV' AND RefNbr = '000686')
	OR (TranType = 'INV' AND RefNbr = '000687')
	OR (TranType = 'INV' AND RefNbr = '000688')
	OR (TranType = 'INV' AND RefNbr = '000689')
	OR (TranType = 'INV' AND RefNbr = '000690')
	OR (TranType = 'INV' AND RefNbr = '000691')
	OR (TranType = 'INV' AND RefNbr = '000692')
	OR (TranType = 'INV' AND RefNbr = '000693')
	OR (TranType = 'INV' AND RefNbr = '000694')
	OR (TranType = 'INV' AND RefNbr = '000695')
	OR (TranType = 'INV' AND RefNbr = '000696')
	OR (TranType = 'INV' AND RefNbr = '000697')
	OR (TranType = 'INV' AND RefNbr = '000698')
	OR (TranType = 'INV' AND RefNbr = '000699')
	OR (TranType = 'INV' AND RefNbr = '000700')
	OR (TranType = 'INV' AND RefNbr = '000701')
	OR (TranType = 'INV' AND RefNbr = '000702')
	OR (TranType = 'INV' AND RefNbr = '000703')
	OR (TranType = 'INV' AND RefNbr = '000704')
	OR (TranType = 'INV' AND RefNbr = '000705')
	OR (TranType = 'INV' AND RefNbr = '000706')
	OR (TranType = 'INV' AND RefNbr = '000707')
	OR (TranType = 'INV' AND RefNbr = '000708')
	OR (TranType = 'INV' AND RefNbr = '000709')
	OR (TranType = 'INV' AND RefNbr = '000710')
	OR (TranType = 'INV' AND RefNbr = '000711')
	OR (TranType = 'INV' AND RefNbr = '000712')
	OR (TranType = 'INV' AND RefNbr = '000713')
	OR (TranType = 'INV' AND RefNbr = '000714')
	OR (TranType = 'INV' AND RefNbr = '000715')
	OR (TranType = 'INV' AND RefNbr = '000716')
	OR (TranType = 'INV' AND RefNbr = '000717')
	OR (TranType = 'INV' AND RefNbr = '000718')
	OR (TranType = 'INV' AND RefNbr = '000719')
	OR (TranType = 'INV' AND RefNbr = '000720')
	OR (TranType = 'INV' AND RefNbr = '000721')
	OR (TranType = 'INV' AND RefNbr = '000722')
	OR (TranType = 'INV' AND RefNbr = '000723')
	OR (TranType = 'INV' AND RefNbr = '000724')
	OR (TranType = 'INV' AND RefNbr = '000725')
	OR (TranType = 'INV' AND RefNbr = '000726')
	OR (TranType = 'INV' AND RefNbr = '000727')
	OR (TranType = 'INV' AND RefNbr = '000728')
	OR (TranType = 'INV' AND RefNbr = '000729')
	OR (TranType = 'INV' AND RefNbr = '000730')
	OR (TranType = 'INV' AND RefNbr = '000731')
	OR (TranType = 'INV' AND RefNbr = '000732')
	OR (TranType = 'INV' AND RefNbr = '000733')
	OR (TranType = 'INV' AND RefNbr = '000734')
	OR (TranType = 'INV' AND RefNbr = '000735')
	OR (TranType = 'INV' AND RefNbr = '000736')
	OR (TranType = 'INV' AND RefNbr = '000737')
	OR (TranType = 'INV' AND RefNbr = '000738')
	OR (TranType = 'INV' AND RefNbr = '000739')
	OR (TranType = 'INV' AND RefNbr = '000740')
	OR (TranType = 'INV' AND RefNbr = '000741')
	OR (TranType = 'INV' AND RefNbr = '000742')
	OR (TranType = 'INV' AND RefNbr = '000743')
	OR (TranType = 'INV' AND RefNbr = '000744')
	OR (TranType = 'INV' AND RefNbr = '000745')
	OR (TranType = 'INV' AND RefNbr = '000746')
	OR (TranType = 'INV' AND RefNbr = '000747')
	OR (TranType = 'INV' AND RefNbr = '000748')
	OR (TranType = 'INV' AND RefNbr = '000749')
	OR (TranType = 'INV' AND RefNbr = '000750')
	OR (TranType = 'INV' AND RefNbr = '000751')
	OR (TranType = 'INV' AND RefNbr = '000752')
	OR (TranType = 'INV' AND RefNbr = '000753')
	OR (TranType = 'INV' AND RefNbr = '000754')
	OR (TranType = 'INV' AND RefNbr = '000755')
	OR (TranType = 'INV' AND RefNbr = '000756')
	OR (TranType = 'INV' AND RefNbr = '000757')
	OR (TranType = 'INV' AND RefNbr = '000758')
	OR (TranType = 'INV' AND RefNbr = '000759')
	OR (TranType = 'INV' AND RefNbr = '000760')
	OR (TranType = 'INV' AND RefNbr = '000761')
	OR (TranType = 'INV' AND RefNbr = '000762')
	OR (TranType = 'INV' AND RefNbr = '000763')
	OR (TranType = 'INV' AND RefNbr = '000764')
	OR (TranType = 'INV' AND RefNbr = '000765')
	OR (TranType = 'INV' AND RefNbr = '000766')
	OR (TranType = 'INV' AND RefNbr = '000767')
	OR (TranType = 'INV' AND RefNbr = '000768')
	OR (TranType = 'INV' AND RefNbr = '000769')
	OR (TranType = 'INV' AND RefNbr = '000770')
	OR (TranType = 'INV' AND RefNbr = '000771')
	OR (TranType = 'INV' AND RefNbr = '000772')
	OR (TranType = 'INV' AND RefNbr = '000773')
	OR (TranType = 'INV' AND RefNbr = '000774')
	OR (TranType = 'INV' AND RefNbr = '000775')
	OR (TranType = 'INV' AND RefNbr = '000776')
	OR (TranType = 'INV' AND RefNbr = '000777')
	OR (TranType = 'INV' AND RefNbr = '000778')
	OR (TranType = 'INV' AND RefNbr = '000779')
	OR (TranType = 'INV' AND RefNbr = '000780')
	OR (TranType = 'INV' AND RefNbr = '000781')
	OR (TranType = 'INV' AND RefNbr = '000782')
	OR (TranType = 'INV' AND RefNbr = '000783')
	OR (TranType = 'INV' AND RefNbr = '000784')
	OR (TranType = 'INV' AND RefNbr = '000785')
	OR (TranType = 'INV' AND RefNbr = '000786')
	OR (TranType = 'INV' AND RefNbr = '000787')
	OR (TranType = 'INV' AND RefNbr = '000788')
	OR (TranType = 'INV' AND RefNbr = '000789')
	OR (TranType = 'INV' AND RefNbr = '000790')
	OR (TranType = 'INV' AND RefNbr = '000791')
	OR (TranType = 'INV' AND RefNbr = '000792')
	OR (TranType = 'INV' AND RefNbr = '000793')
	OR (TranType = 'INV' AND RefNbr = '000794')
	OR (TranType = 'INV' AND RefNbr = '000795')
	OR (TranType = 'INV' AND RefNbr = '000796')
	OR (TranType = 'INV' AND RefNbr = '000797')
	OR (TranType = 'INV' AND RefNbr = '000798')
	OR (TranType = 'INV' AND RefNbr = '000799')
	OR (TranType = 'INV' AND RefNbr = '000800')
	OR (TranType = 'INV' AND RefNbr = '000801')
	OR (TranType = 'INV' AND RefNbr = '000802')
	OR (TranType = 'INV' AND RefNbr = '000803')
	OR (TranType = 'INV' AND RefNbr = '000804')
	OR (TranType = 'INV' AND RefNbr = '000805')
	OR (TranType = 'INV' AND RefNbr = '000806')
	OR (TranType = 'INV' AND RefNbr = '000807')
	OR (TranType = 'INV' AND RefNbr = '000808')
	OR (TranType = 'INV' AND RefNbr = '000809')
	OR (TranType = 'INV' AND RefNbr = '000810')
	OR (TranType = 'INV' AND RefNbr = '000811')
	OR (TranType = 'INV' AND RefNbr = '000812')
	OR (TranType = 'INV' AND RefNbr = '000813')
	OR (TranType = 'INV' AND RefNbr = '000814')
	OR (TranType = 'INV' AND RefNbr = '000815')
	OR (TranType = 'INV' AND RefNbr = '000816')
	OR (TranType = 'INV' AND RefNbr = '000817')
	OR (TranType = 'INV' AND RefNbr = '000818')
	OR (TranType = 'INV' AND RefNbr = '000819')
	OR (TranType = 'INV' AND RefNbr = '000820')
	OR (TranType = 'INV' AND RefNbr = '000821')
	OR (TranType = 'INV' AND RefNbr = '000822')
	OR (TranType = 'INV' AND RefNbr = '000823')
	OR (TranType = 'INV' AND RefNbr = '000824')
	OR (TranType = 'INV' AND RefNbr = '000825')
	OR (TranType = 'INV' AND RefNbr = '000826')
	OR (TranType = 'INV' AND RefNbr = '000827')
	OR (TranType = 'INV' AND RefNbr = '000828')
	OR (TranType = 'INV' AND RefNbr = '000829')
	OR (TranType = 'INV' AND RefNbr = '000830')
	OR (TranType = 'INV' AND RefNbr = '000831')
	OR (TranType = 'INV' AND RefNbr = '000832')
	OR (TranType = 'INV' AND RefNbr = '000833')
	OR (TranType = 'INV' AND RefNbr = '000834')
	OR (TranType = 'INV' AND RefNbr = '000835')
	OR (TranType = 'INV' AND RefNbr = '000836')
	OR (TranType = 'INV' AND RefNbr = '000837')
	OR (TranType = 'INV' AND RefNbr = '000838')
	OR (TranType = 'INV' AND RefNbr = '000839')
	OR (TranType = 'INV' AND RefNbr = '000840')
	OR (TranType = 'INV' AND RefNbr = '000841')
	OR (TranType = 'INV' AND RefNbr = '000842')
	OR (TranType = 'INV' AND RefNbr = '000843')
	OR (TranType = 'INV' AND RefNbr = '000844')
	OR (TranType = 'INV' AND RefNbr = '000845')
	OR (TranType = 'INV' AND RefNbr = '000846')
	OR (TranType = 'INV' AND RefNbr = '000847')
	OR (TranType = 'INV' AND RefNbr = '000848')
	OR (TranType = 'INV' AND RefNbr = '000849')
	OR (TranType = 'INV' AND RefNbr = '000850')
	OR (TranType = 'INV' AND RefNbr = '000851')
	OR (TranType = 'INV' AND RefNbr = '000852')
	OR (TranType = 'INV' AND RefNbr = '000853')
	OR (TranType = 'INV' AND RefNbr = '000854')
	OR (TranType = 'INV' AND RefNbr = '000855')
	OR (TranType = 'INV' AND RefNbr = '000856')
	OR (TranType = 'INV' AND RefNbr = '000857')
	OR (TranType = 'INV' AND RefNbr = '000858')
	OR (TranType = 'INV' AND RefNbr = '000859')
	OR (TranType = 'INV' AND RefNbr = '000860')
	OR (TranType = 'INV' AND RefNbr = '000861')
	OR (TranType = 'INV' AND RefNbr = '000862')
	OR (TranType = 'INV' AND RefNbr = '000863')
	OR (TranType = 'INV' AND RefNbr = '000864')
	OR (TranType = 'INV' AND RefNbr = '000865')
	OR (TranType = 'INV' AND RefNbr = '000866')
	OR (TranType = 'INV' AND RefNbr = '000867')
	OR (TranType = 'INV' AND RefNbr = '000868')
	OR (TranType = 'INV' AND RefNbr = '000869')
	OR (TranType = 'INV' AND RefNbr = '000870')
	OR (TranType = 'INV' AND RefNbr = '000871')
	OR (TranType = 'INV' AND RefNbr = '000872')
	OR (TranType = 'INV' AND RefNbr = '000873')
	OR (TranType = 'INV' AND RefNbr = '000874')
	OR (TranType = 'INV' AND RefNbr = '000875')
	OR (TranType = 'INV' AND RefNbr = '000876')
	OR (TranType = 'INV' AND RefNbr = '000877')
	OR (TranType = 'INV' AND RefNbr = '000878')
	OR (TranType = 'INV' AND RefNbr = '000879')
	OR (TranType = 'INV' AND RefNbr = '000880')
	OR (TranType = 'INV' AND RefNbr = '000881')
	OR (TranType = 'INV' AND RefNbr = '000882')
	OR (TranType = 'INV' AND RefNbr = '000883')
	OR (TranType = 'INV' AND RefNbr = '000884')
	OR (TranType = 'INV' AND RefNbr = '000885')
	OR (TranType = 'INV' AND RefNbr = '000886')
	OR (TranType = 'INV' AND RefNbr = '000887')
	OR (TranType = 'INV' AND RefNbr = '000888')
	OR (TranType = 'INV' AND RefNbr = '000889')
	OR (TranType = 'INV' AND RefNbr = '000890')
	OR (TranType = 'INV' AND RefNbr = '000891')
	OR (TranType = 'INV' AND RefNbr = '000892')
	OR (TranType = 'INV' AND RefNbr = '000893')
	OR (TranType = 'INV' AND RefNbr = '000894')
	OR (TranType = 'INV' AND RefNbr = '000895')
	OR (TranType = 'INV' AND RefNbr = '000896')
	OR (TranType = 'INV' AND RefNbr = '000897')
	OR (TranType = 'INV' AND RefNbr = '000898')
	OR (TranType = 'INV' AND RefNbr = '000899')
	OR (TranType = 'INV' AND RefNbr = '000900')
	OR (TranType = 'INV' AND RefNbr = '000901')
	OR (TranType = 'INV' AND RefNbr = '000902')
	OR (TranType = 'INV' AND RefNbr = '000903')
	OR (TranType = 'INV' AND RefNbr = '000904')
	OR (TranType = 'INV' AND RefNbr = '000905')
	OR (TranType = 'INV' AND RefNbr = '000906')
	OR (TranType = 'INV' AND RefNbr = '000907')
	OR (TranType = 'INV' AND RefNbr = '000908')
	OR (TranType = 'INV' AND RefNbr = '000909')
	OR (TranType = 'INV' AND RefNbr = '000910')
	OR (TranType = 'INV' AND RefNbr = '000911')
	OR (TranType = 'INV' AND RefNbr = '000912')
	OR (TranType = 'INV' AND RefNbr = '000913')
	OR (TranType = 'INV' AND RefNbr = '000914')
	OR (TranType = 'INV' AND RefNbr = '000915')
	OR (TranType = 'INV' AND RefNbr = '000916')
	OR (TranType = 'INV' AND RefNbr = '000917')
	OR (TranType = 'INV' AND RefNbr = '000918')
	OR (TranType = 'INV' AND RefNbr = '000919')
	OR (TranType = 'INV' AND RefNbr = '000920')
	OR (TranType = 'INV' AND RefNbr = '000921')
	OR (TranType = 'INV' AND RefNbr = '000922')
	OR (TranType = 'INV' AND RefNbr = '000923')
	OR (TranType = 'INV' AND RefNbr = '000924')
	OR (TranType = 'INV' AND RefNbr = '000925')
	OR (TranType = 'INV' AND RefNbr = '000926')
	OR (TranType = 'INV' AND RefNbr = '000927')
	OR (TranType = 'INV' AND RefNbr = '000928')
	OR (TranType = 'INV' AND RefNbr = '000929')
	OR (TranType = 'INV' AND RefNbr = '000930')
	OR (TranType = 'INV' AND RefNbr = '000931')
	OR (TranType = 'INV' AND RefNbr = '000932')
	OR (TranType = 'INV' AND RefNbr = '000933')
	OR (TranType = 'INV' AND RefNbr = '000934')
	OR (TranType = 'INV' AND RefNbr = '000935')
	OR (TranType = 'INV' AND RefNbr = '000936')
	OR (TranType = 'INV' AND RefNbr = '000937')
	OR (TranType = 'INV' AND RefNbr = '000938')
	OR (TranType = 'INV' AND RefNbr = '000939')
	OR (TranType = 'INV' AND RefNbr = '000940')
	OR (TranType = 'INV' AND RefNbr = '000941')
	OR (TranType = 'INV' AND RefNbr = '000942')
	OR (TranType = 'INV' AND RefNbr = '000943')
	OR (TranType = 'INV' AND RefNbr = '000944')
	OR (TranType = 'INV' AND RefNbr = '000945')
	OR (TranType = 'INV' AND RefNbr = '000946')
	OR (TranType = 'INV' AND RefNbr = '000947')
	OR (TranType = 'INV' AND RefNbr = '000948')
	OR (TranType = 'INV' AND RefNbr = '000949')
	OR (TranType = 'INV' AND RefNbr = '000950')
	OR (TranType = 'INV' AND RefNbr = '000951')
	OR (TranType = 'INV' AND RefNbr = '000952')
	OR (TranType = 'INV' AND RefNbr = '000953')
	OR (TranType = 'INV' AND RefNbr = '000954')
	OR (TranType = 'INV' AND RefNbr = '000955')
	OR (TranType = 'INV' AND RefNbr = '000956')
	OR (TranType = 'INV' AND RefNbr = '000957')
	OR (TranType = 'INV' AND RefNbr = '000958')
	OR (TranType = 'INV' AND RefNbr = '000959')
	OR (TranType = 'INV' AND RefNbr = '000960')
	OR (TranType = 'INV' AND RefNbr = '000961')
	OR (TranType = 'INV' AND RefNbr = '000962')
	OR (TranType = 'INV' AND RefNbr = '000963')
	OR (TranType = 'INV' AND RefNbr = '000964')
	OR (TranType = 'INV' AND RefNbr = '000965')
	OR (TranType = 'INV' AND RefNbr = '000966')
	OR (TranType = 'INV' AND RefNbr = '000967')
	OR (TranType = 'INV' AND RefNbr = '000968')
	OR (TranType = 'INV' AND RefNbr = '000969')
	OR (TranType = 'INV' AND RefNbr = '000970')
	OR (TranType = 'INV' AND RefNbr = '000971')
	OR (TranType = 'INV' AND RefNbr = '000972')
	OR (TranType = 'INV' AND RefNbr = '000973')
	OR (TranType = 'INV' AND RefNbr = '000974')
	OR (TranType = 'INV' AND RefNbr = '000975')
	OR (TranType = 'INV' AND RefNbr = '000976')
	OR (TranType = 'INV' AND RefNbr = '000977')
	OR (TranType = 'INV' AND RefNbr = '000978')
	OR (TranType = 'INV' AND RefNbr = '000979')
	OR (TranType = 'INV' AND RefNbr = '000980')
	OR (TranType = 'INV' AND RefNbr = '000981')
	OR (TranType = 'INV' AND RefNbr = '000982')
	OR (TranType = 'INV' AND RefNbr = '000983')
	OR (TranType = 'INV' AND RefNbr = '000984')
	OR (TranType = 'INV' AND RefNbr = '000985')
	OR (TranType = 'INV' AND RefNbr = '000986')
	OR (TranType = 'INV' AND RefNbr = '000987')
	OR (TranType = 'INV' AND RefNbr = '000988')
	OR (TranType = 'INV' AND RefNbr = '000989')
	OR (TranType = 'INV' AND RefNbr = '000990')
	OR (TranType = 'INV' AND RefNbr = '000991')
	OR (TranType = 'INV' AND RefNbr = '000992')
	OR (TranType = 'INV' AND RefNbr = '000993')
	OR (TranType = 'INV' AND RefNbr = '000994')
	OR (TranType = 'INV' AND RefNbr = '000995')
	OR (TranType = 'INV' AND RefNbr = '000996')
	OR (TranType = 'INV' AND RefNbr = '000997')
	OR (TranType = 'INV' AND RefNbr = '000998')
	OR (TranType = 'INV' AND RefNbr = '000999')
	OR (TranType = 'INV' AND RefNbr = '001000')
	OR (TranType = 'INV' AND RefNbr = '001001')
	OR (TranType = 'INV' AND RefNbr = '001002')
	OR (TranType = 'INV' AND RefNbr = '001003')
	OR (TranType = 'INV' AND RefNbr = '001004')
	OR (TranType = 'INV' AND RefNbr = '001005')
	OR (TranType = 'INV' AND RefNbr = '001006')
	OR (TranType = 'INV' AND RefNbr = '001007')
	OR (TranType = 'INV' AND RefNbr = '001008')
	OR (TranType = 'INV' AND RefNbr = '001009')
	OR (TranType = 'INV' AND RefNbr = '001010')
	OR (TranType = 'INV' AND RefNbr = '001011')
	OR (TranType = 'INV' AND RefNbr = '001012')
	OR (TranType = 'INV' AND RefNbr = '001013')
	OR (TranType = 'INV' AND RefNbr = '001014')
	OR (TranType = 'INV' AND RefNbr = '001015')
	OR (TranType = 'INV' AND RefNbr = '001016')
	OR (TranType = 'INV' AND RefNbr = '001017')
	OR (TranType = 'INV' AND RefNbr = '001018')
	OR (TranType = 'INV' AND RefNbr = '001019')
	OR (TranType = 'INV' AND RefNbr = '001020')
	OR (TranType = 'INV' AND RefNbr = '001021')
	OR (TranType = 'INV' AND RefNbr = '001022')
	OR (TranType = 'INV' AND RefNbr = '001023')
	OR (TranType = 'INV' AND RefNbr = '001024')
	OR (TranType = 'INV' AND RefNbr = '001025')
	OR (TranType = 'INV' AND RefNbr = '001026')
	OR (TranType = 'INV' AND RefNbr = '001027')
	OR (TranType = 'INV' AND RefNbr = '001028')
	OR (TranType = 'INV' AND RefNbr = '001029')
	OR (TranType = 'INV' AND RefNbr = '001030')
	OR (TranType = 'INV' AND RefNbr = '001031')
	OR (TranType = 'INV' AND RefNbr = '001032')
	OR (TranType = 'INV' AND RefNbr = '001033')
	OR (TranType = 'INV' AND RefNbr = '001034')
	OR (TranType = 'INV' AND RefNbr = '001035')
	OR (TranType = 'INV' AND RefNbr = '001036')
	OR (TranType = 'INV' AND RefNbr = '001037')
	OR (TranType = 'INV' AND RefNbr = '001038')
	OR (TranType = 'INV' AND RefNbr = '001039')
	OR (TranType = 'INV' AND RefNbr = '001040')
	OR (TranType = 'INV' AND RefNbr = '001041')
	OR (TranType = 'INV' AND RefNbr = '001042')
	OR (TranType = 'INV' AND RefNbr = '001043')
	OR (TranType = 'INV' AND RefNbr = '001044')
	OR (TranType = 'INV' AND RefNbr = '001045')
	OR (TranType = 'INV' AND RefNbr = '001046')
	OR (TranType = 'INV' AND RefNbr = '001047')
	OR (TranType = 'INV' AND RefNbr = '001048')
	OR (TranType = 'INV' AND RefNbr = '001049')
	OR (TranType = 'INV' AND RefNbr = '001050')
	OR (TranType = 'INV' AND RefNbr = '001051')
	OR (TranType = 'INV' AND RefNbr = '001052')
	OR (TranType = 'INV' AND RefNbr = '001053')
	OR (TranType = 'INV' AND RefNbr = '001054')
	OR (TranType = 'INV' AND RefNbr = '001055')
	OR (TranType = 'INV' AND RefNbr = '001056')
	OR (TranType = 'INV' AND RefNbr = '001057')
	OR (TranType = 'INV' AND RefNbr = '001058')
	OR (TranType = 'INV' AND RefNbr = '001059')
	OR (TranType = 'INV' AND RefNbr = '001060')
	OR (TranType = 'INV' AND RefNbr = '001061')
	OR (TranType = 'INV' AND RefNbr = '001062')
	OR (TranType = 'INV' AND RefNbr = '001063')
	OR (TranType = 'INV' AND RefNbr = '001064')
	OR (TranType = 'INV' AND RefNbr = '001065')
	OR (TranType = 'INV' AND RefNbr = '001066')
	OR (TranType = 'INV' AND RefNbr = '001067')
	OR (TranType = 'INV' AND RefNbr = '001068')
	OR (TranType = 'INV' AND RefNbr = '001069')
	OR (TranType = 'INV' AND RefNbr = '001070')
	OR (TranType = 'INV' AND RefNbr = '001071')
	OR (TranType = 'INV' AND RefNbr = '001072')
	OR (TranType = 'INV' AND RefNbr = '001073')
	OR (TranType = 'INV' AND RefNbr = '001074')
	OR (TranType = 'INV' AND RefNbr = '001075')
	OR (TranType = 'INV' AND RefNbr = '001076')
	OR (TranType = 'INV' AND RefNbr = '001077')
	OR (TranType = 'INV' AND RefNbr = '001078')
	OR (TranType = 'INV' AND RefNbr = '001079')
	OR (TranType = 'INV' AND RefNbr = '001080')
	OR (TranType = 'INV' AND RefNbr = '001081')
	OR (TranType = 'INV' AND RefNbr = '001082')
	OR (TranType = 'INV' AND RefNbr = '001083')
	OR (TranType = 'INV' AND RefNbr = '001084')
	OR (TranType = 'INV' AND RefNbr = '001085')
	OR (TranType = 'INV' AND RefNbr = '001086')
	OR (TranType = 'INV' AND RefNbr = '001087')
	OR (TranType = 'INV' AND RefNbr = '001088')
	OR (TranType = 'INV' AND RefNbr = '001089')
	OR (TranType = 'INV' AND RefNbr = '001090')
	OR (TranType = 'INV' AND RefNbr = '001091')
	OR (TranType = 'INV' AND RefNbr = '001092')
	OR (TranType = 'INV' AND RefNbr = '001093')
	OR (TranType = 'INV' AND RefNbr = '001094')
	OR (TranType = 'INV' AND RefNbr = '001095')
	OR (TranType = 'INV' AND RefNbr = '001096')
	OR (TranType = 'INV' AND RefNbr = '001097')
	OR (TranType = 'INV' AND RefNbr = '001098')
	OR (TranType = 'INV' AND RefNbr = '001099')
	OR (TranType = 'INV' AND RefNbr = '001100')
	OR (TranType = 'INV' AND RefNbr = '001101')
	OR (TranType = 'INV' AND RefNbr = '001102')
	OR (TranType = 'INV' AND RefNbr = '001103')
	OR (TranType = 'INV' AND RefNbr = '001104')
	OR (TranType = 'INV' AND RefNbr = '001105')
	OR (TranType = 'INV' AND RefNbr = '001106')
	OR (TranType = 'INV' AND RefNbr = '001107')
	OR (TranType = 'INV' AND RefNbr = '001108')
	OR (TranType = 'INV' AND RefNbr = '001109')
	OR (TranType = 'INV' AND RefNbr = '001110')
	OR (TranType = 'INV' AND RefNbr = '001111')
	OR (TranType = 'INV' AND RefNbr = '001112')
	OR (TranType = 'INV' AND RefNbr = '001113')
	OR (TranType = 'INV' AND RefNbr = '001114')
	OR (TranType = 'INV' AND RefNbr = '001115')
	OR (TranType = 'INV' AND RefNbr = '001116')
	OR (TranType = 'INV' AND RefNbr = '001117')
	OR (TranType = 'INV' AND RefNbr = '001118')
	OR (TranType = 'INV' AND RefNbr = '001119')
	OR (TranType = 'INV' AND RefNbr = '001120')
	OR (TranType = 'INV' AND RefNbr = '001121')
	OR (TranType = 'INV' AND RefNbr = '001122')
	OR (TranType = 'INV' AND RefNbr = '001123')
	OR (TranType = 'INV' AND RefNbr = '001124')
	OR (TranType = 'INV' AND RefNbr = '001125')
	OR (TranType = 'INV' AND RefNbr = '001126')
	OR (TranType = 'INV' AND RefNbr = '001127')
	OR (TranType = 'INV' AND RefNbr = '001128')
	OR (TranType = 'INV' AND RefNbr = '001129')
	OR (TranType = 'INV' AND RefNbr = '001130')
	OR (TranType = 'INV' AND RefNbr = '001131')
	OR (TranType = 'INV' AND RefNbr = '001132')
	OR (TranType = 'INV' AND RefNbr = '001133')
	OR (TranType = 'INV' AND RefNbr = '001134')
	OR (TranType = 'INV' AND RefNbr = '001135')
	OR (TranType = 'INV' AND RefNbr = '001136')
	OR (TranType = 'INV' AND RefNbr = '001137')
	OR (TranType = 'INV' AND RefNbr = '001138')
	OR (TranType = 'INV' AND RefNbr = '001139')
	OR (TranType = 'INV' AND RefNbr = '001140')
	OR (TranType = 'INV' AND RefNbr = '001141')
	OR (TranType = 'INV' AND RefNbr = '001142')
	OR (TranType = 'INV' AND RefNbr = '001143')
	OR (TranType = 'INV' AND RefNbr = '001144')
	OR (TranType = 'INV' AND RefNbr = '001145')
	OR (TranType = 'INV' AND RefNbr = '001146')
	OR (TranType = 'INV' AND RefNbr = '001147')
	OR (TranType = 'INV' AND RefNbr = '001148')
	OR (TranType = 'INV' AND RefNbr = '001149')
	OR (TranType = 'INV' AND RefNbr = '001150')
	OR (TranType = 'INV' AND RefNbr = '001151')
	OR (TranType = 'INV' AND RefNbr = '001152')
	OR (TranType = 'INV' AND RefNbr = '001153')
	OR (TranType = 'INV' AND RefNbr = '001154')
	OR (TranType = 'INV' AND RefNbr = '001155')
	OR (TranType = 'INV' AND RefNbr = '001156')
	OR (TranType = 'INV' AND RefNbr = '001157')
	OR (TranType = 'INV' AND RefNbr = '001158')
	OR (TranType = 'INV' AND RefNbr = '001159')
	OR (TranType = 'INV' AND RefNbr = '001160')
	OR (TranType = 'INV' AND RefNbr = '001161')
	OR (TranType = 'INV' AND RefNbr = '001162')
	OR (TranType = 'INV' AND RefNbr = '001163')
	OR (TranType = 'INV' AND RefNbr = '001164')
	OR (TranType = 'INV' AND RefNbr = '001165')
	OR (TranType = 'INV' AND RefNbr = '001166')
	OR (TranType = 'INV' AND RefNbr = '001167')
	OR (TranType = 'INV' AND RefNbr = '001168')
	OR (TranType = 'INV' AND RefNbr = '001169')
	OR (TranType = 'INV' AND RefNbr = '001170')
	OR (TranType = 'INV' AND RefNbr = '001171')
	OR (TranType = 'INV' AND RefNbr = '001172')
	OR (TranType = 'INV' AND RefNbr = '001173')
	OR (TranType = 'INV' AND RefNbr = '001174')
	OR (TranType = 'INV' AND RefNbr = '001175')
	OR (TranType = 'INV' AND RefNbr = '001176')
	OR (TranType = 'INV' AND RefNbr = '001177')
	OR (TranType = 'INV' AND RefNbr = '001178')
	OR (TranType = 'INV' AND RefNbr = '001179')
	OR (TranType = 'INV' AND RefNbr = '001180')
	OR (TranType = 'INV' AND RefNbr = '001181')
	OR (TranType = 'INV' AND RefNbr = '001182')
	OR (TranType = 'INV' AND RefNbr = '001183')
	OR (TranType = 'INV' AND RefNbr = '001184')
	OR (TranType = 'INV' AND RefNbr = '001185')
	OR (TranType = 'INV' AND RefNbr = '001186')
	OR (TranType = 'INV' AND RefNbr = '001187')
	OR (TranType = 'INV' AND RefNbr = '001188')
	OR (TranType = 'INV' AND RefNbr = '001189')
	OR (TranType = 'INV' AND RefNbr = '001190')
	OR (TranType = 'INV' AND RefNbr = '001191')
	OR (TranType = 'INV' AND RefNbr = '001192')
	OR (TranType = 'INV' AND RefNbr = '001193')
	OR (TranType = 'INV' AND RefNbr = '001194')
	OR (TranType = 'INV' AND RefNbr = '001195')
	OR (TranType = 'INV' AND RefNbr = '001196')
	OR (TranType = 'INV' AND RefNbr = '001197')
	OR (TranType = 'INV' AND RefNbr = '001198')
	OR (TranType = 'INV' AND RefNbr = '001199')
	OR (TranType = 'INV' AND RefNbr = '001200')
	OR (TranType = 'INV' AND RefNbr = '001201')
	OR (TranType = 'INV' AND RefNbr = '001202')
	OR (TranType = 'INV' AND RefNbr = '001203')
	OR (TranType = 'INV' AND RefNbr = '001204')
	OR (TranType = 'INV' AND RefNbr = '001205')
	OR (TranType = 'INV' AND RefNbr = '001206')
	OR (TranType = 'INV' AND RefNbr = '001207')
	OR (TranType = 'INV' AND RefNbr = '001208')
	OR (TranType = 'INV' AND RefNbr = '001209')
	OR (TranType = 'INV' AND RefNbr = '001210')
	OR (TranType = 'INV' AND RefNbr = '001211')
	OR (TranType = 'INV' AND RefNbr = '001212')
	OR (TranType = 'INV' AND RefNbr = '001213')
	OR (TranType = 'INV' AND RefNbr = '001214')
	OR (TranType = 'INV' AND RefNbr = '001215')
	OR (TranType = 'INV' AND RefNbr = '001216')
	OR (TranType = 'INV' AND RefNbr = '001217')
	OR (TranType = 'INV' AND RefNbr = '001218')
	OR (TranType = 'INV' AND RefNbr = '001219')
	OR (TranType = 'INV' AND RefNbr = '001220')
	OR (TranType = 'INV' AND RefNbr = '001221')
	OR (TranType = 'INV' AND RefNbr = '001222')
	OR (TranType = 'INV' AND RefNbr = '001223')
	OR (TranType = 'INV' AND RefNbr = '001224')
	OR (TranType = 'INV' AND RefNbr = '001225')
	OR (TranType = 'INV' AND RefNbr = '001226')
	OR (TranType = 'INV' AND RefNbr = '001227')
	OR (TranType = 'INV' AND RefNbr = '001228')
	OR (TranType = 'INV' AND RefNbr = '001229')
	OR (TranType = 'INV' AND RefNbr = '001230')
	OR (TranType = 'INV' AND RefNbr = '001231')
	OR (TranType = 'INV' AND RefNbr = '001232')
	OR (TranType = 'INV' AND RefNbr = '001233')
	OR (TranType = 'INV' AND RefNbr = '001234')
	OR (TranType = 'INV' AND RefNbr = '001235')
	OR (TranType = 'INV' AND RefNbr = '001236')
	OR (TranType = 'INV' AND RefNbr = '001237')
	OR (TranType = 'INV' AND RefNbr = '001238')
	OR (TranType = 'INV' AND RefNbr = '001239')
	OR (TranType = 'INV' AND RefNbr = '001240')
	OR (TranType = 'INV' AND RefNbr = '001241')
	OR (TranType = 'INV' AND RefNbr = '001242')
	OR (TranType = 'INV' AND RefNbr = '001243')
	OR (TranType = 'INV' AND RefNbr = '001244')
	OR (TranType = 'INV' AND RefNbr = '001245')
	OR (TranType = 'INV' AND RefNbr = '001246')
	OR (TranType = 'INV' AND RefNbr = '001247')
	OR (TranType = 'INV' AND RefNbr = '001248')
	OR (TranType = 'INV' AND RefNbr = '001249')
	OR (TranType = 'INV' AND RefNbr = '001250')
	OR (TranType = 'INV' AND RefNbr = '001251')
	OR (TranType = 'INV' AND RefNbr = '001252')
	OR (TranType = 'INV' AND RefNbr = '001253')
	OR (TranType = 'INV' AND RefNbr = '001254')
	OR (TranType = 'INV' AND RefNbr = '001255')
	OR (TranType = 'INV' AND RefNbr = '001256')
	OR (TranType = 'INV' AND RefNbr = '001257')
	OR (TranType = 'INV' AND RefNbr = '001258')
	OR (TranType = 'INV' AND RefNbr = '001259')
	OR (TranType = 'INV' AND RefNbr = '001260')
	OR (TranType = 'INV' AND RefNbr = '001263')
	OR (TranType = 'INV' AND RefNbr = '001264')
	OR (TranType = 'INV' AND RefNbr = '001265')
	OR (TranType = 'INV' AND RefNbr = '001266')
	OR (TranType = 'INV' AND RefNbr = '001267')
	OR (TranType = 'INV' AND RefNbr = '001268')
	OR (TranType = 'INV' AND RefNbr = '001269')
	OR (TranType = 'INV' AND RefNbr = '001270')
	OR (TranType = 'INV' AND RefNbr = '001271')
	OR (TranType = 'INV' AND RefNbr = '001272')
	OR (TranType = 'INV' AND RefNbr = '001273')
	OR (TranType = 'INV' AND RefNbr = '001274')
	OR (TranType = 'INV' AND RefNbr = '001275')
	OR (TranType = 'INV' AND RefNbr = '001276')
	OR (TranType = 'INV' AND RefNbr = '001277')
	OR (TranType = 'INV' AND RefNbr = '001278')
	OR (TranType = 'INV' AND RefNbr = '001279')
	OR (TranType = 'INV' AND RefNbr = '001280')
	OR (TranType = 'INV' AND RefNbr = '001281')
	OR (TranType = 'INV' AND RefNbr = '001282')
	OR (TranType = 'INV' AND RefNbr = '001283')
	OR (TranType = 'INV' AND RefNbr = '001284')
	OR (TranType = 'INV' AND RefNbr = '001285')
	OR (TranType = 'INV' AND RefNbr = '001286')
	OR (TranType = 'INV' AND RefNbr = '001287')
	OR (TranType = 'INV' AND RefNbr = '001288')
	OR (TranType = 'INV' AND RefNbr = '001289')
	OR (TranType = 'INV' AND RefNbr = '001290')
	OR (TranType = 'INV' AND RefNbr = '001291')
	OR (TranType = 'INV' AND RefNbr = '001292')
	OR (TranType = 'INV' AND RefNbr = '001293')
	OR (TranType = 'INV' AND RefNbr = '001294')
	OR (TranType = 'INV' AND RefNbr = '001295')
	OR (TranType = 'INV' AND RefNbr = '001296')
	OR (TranType = 'INV' AND RefNbr = '001297')
	OR (TranType = 'INV' AND RefNbr = '001298')
	OR (TranType = 'INV' AND RefNbr = '001299')
	OR (TranType = 'INV' AND RefNbr = '001300')
	OR (TranType = 'INV' AND RefNbr = '001301')
	OR (TranType = 'INV' AND RefNbr = '001302')
	OR (TranType = 'INV' AND RefNbr = '001303')
	OR (TranType = 'INV' AND RefNbr = '001304')
	OR (TranType = 'INV' AND RefNbr = '001305')
	OR (TranType = 'INV' AND RefNbr = '001306')
	OR (TranType = 'INV' AND RefNbr = '001307')
	OR (TranType = 'INV' AND RefNbr = '001308')
	OR (TranType = 'INV' AND RefNbr = '001309')
	OR (TranType = 'INV' AND RefNbr = '001310')
	OR (TranType = 'INV' AND RefNbr = '001311')
	OR (TranType = 'INV' AND RefNbr = '001312')
	OR (TranType = 'INV' AND RefNbr = '001313')
	OR (TranType = 'INV' AND RefNbr = '001314')
	OR (TranType = 'INV' AND RefNbr = '001315')
	OR (TranType = 'INV' AND RefNbr = '001316')
	OR (TranType = 'INV' AND RefNbr = '001317')
	OR (TranType = 'INV' AND RefNbr = '001318')
	OR (TranType = 'INV' AND RefNbr = '001319')
	OR (TranType = 'INV' AND RefNbr = '001320')
	OR (TranType = 'INV' AND RefNbr = '001321')
	OR (TranType = 'INV' AND RefNbr = '001322')
	OR (TranType = 'INV' AND RefNbr = '001323')
	OR (TranType = 'INV' AND RefNbr = '001324')
	OR (TranType = 'INV' AND RefNbr = '001325')
	OR (TranType = 'INV' AND RefNbr = '001326')
	OR (TranType = 'INV' AND RefNbr = '001327')
	OR (TranType = 'INV' AND RefNbr = '001328')
	OR (TranType = 'INV' AND RefNbr = '001329')
	OR (TranType = 'INV' AND RefNbr = '001330')
	OR (TranType = 'INV' AND RefNbr = '001331')
	OR (TranType = 'INV' AND RefNbr = '001332')
	OR (TranType = 'INV' AND RefNbr = '001333')
	OR (TranType = 'INV' AND RefNbr = '001334')
	OR (TranType = 'INV' AND RefNbr = '001335')
	OR (TranType = 'INV' AND RefNbr = '001336')
	OR (TranType = 'INV' AND RefNbr = '001337')
	OR (TranType = 'INV' AND RefNbr = '001338')
	OR (TranType = 'INV' AND RefNbr = '001339')
	OR (TranType = 'INV' AND RefNbr = '001340')
	OR (TranType = 'INV' AND RefNbr = '001341')
	OR (TranType = 'INV' AND RefNbr = '001342')
	OR (TranType = 'INV' AND RefNbr = '001343')
	OR (TranType = 'INV' AND RefNbr = '001344')
	OR (TranType = 'INV' AND RefNbr = '001345')
	OR (TranType = 'INV' AND RefNbr = '001346')
	OR (TranType = 'INV' AND RefNbr = '001347')
	OR (TranType = 'INV' AND RefNbr = '001348')
	OR (TranType = 'INV' AND RefNbr = '001349')
	OR (TranType = 'INV' AND RefNbr = '001350')
	OR (TranType = 'INV' AND RefNbr = '001351')
	OR (TranType = 'INV' AND RefNbr = '001352')
	OR (TranType = 'INV' AND RefNbr = '001353')
	OR (TranType = 'INV' AND RefNbr = '001354')
	OR (TranType = 'INV' AND RefNbr = '001355')
	OR (TranType = 'INV' AND RefNbr = '001356')
	OR (TranType = 'INV' AND RefNbr = '001357')
	OR (TranType = 'INV' AND RefNbr = '001358')
	OR (TranType = 'INV' AND RefNbr = '001359')
	OR (TranType = 'INV' AND RefNbr = '001360')
	OR (TranType = 'INV' AND RefNbr = '001361')
	OR (TranType = 'INV' AND RefNbr = '001362')
	OR (TranType = 'INV' AND RefNbr = '001363')
	OR (TranType = 'INV' AND RefNbr = '001364')
	OR (TranType = 'INV' AND RefNbr = '001365')
	OR (TranType = 'INV' AND RefNbr = '001366')
	OR (TranType = 'INV' AND RefNbr = '001367')
	OR (TranType = 'INV' AND RefNbr = '001368')
	OR (TranType = 'INV' AND RefNbr = '001369')
	OR (TranType = 'INV' AND RefNbr = '001370')
	OR (TranType = 'INV' AND RefNbr = '001371')
	OR (TranType = 'INV' AND RefNbr = '001372')
	OR (TranType = 'INV' AND RefNbr = '001373')
	OR (TranType = 'INV' AND RefNbr = '001374')
	OR (TranType = 'INV' AND RefNbr = '001375')
	OR (TranType = 'INV' AND RefNbr = '001376')
	OR (TranType = 'INV' AND RefNbr = '001377')
	OR (TranType = 'INV' AND RefNbr = '001378')
	OR (TranType = 'INV' AND RefNbr = '001379')
	OR (TranType = 'INV' AND RefNbr = '001380')
	OR (TranType = 'INV' AND RefNbr = '001381')
	OR (TranType = 'INV' AND RefNbr = '001382')
	OR (TranType = 'INV' AND RefNbr = '001383')
	OR (TranType = 'INV' AND RefNbr = '001384')
	OR (TranType = 'INV' AND RefNbr = '001385')
	OR (TranType = 'INV' AND RefNbr = '001386')
	OR (TranType = 'INV' AND RefNbr = '001387')
	OR (TranType = 'INV' AND RefNbr = '001388')
	OR (TranType = 'INV' AND RefNbr = '001389')
	OR (TranType = 'INV' AND RefNbr = '001390')
	OR (TranType = 'INV' AND RefNbr = '001391')
	OR (TranType = 'INV' AND RefNbr = '001392')
	OR (TranType = 'INV' AND RefNbr = '001393')
	OR (TranType = 'INV' AND RefNbr = '001394')
	OR (TranType = 'INV' AND RefNbr = '001395')
	OR (TranType = 'INV' AND RefNbr = '001396')
	OR (TranType = 'INV' AND RefNbr = '001397')
	OR (TranType = 'INV' AND RefNbr = '001398')
	OR (TranType = 'INV' AND RefNbr = '001399')
	OR (TranType = 'INV' AND RefNbr = '001400')
	OR (TranType = 'INV' AND RefNbr = '001401')
	OR (TranType = 'INV' AND RefNbr = '001402')
	OR (TranType = 'INV' AND RefNbr = '001403')
	OR (TranType = 'INV' AND RefNbr = '001404')
	OR (TranType = 'INV' AND RefNbr = '001405')
	OR (TranType = 'INV' AND RefNbr = '001406')
	OR (TranType = 'INV' AND RefNbr = '001407')
	OR (TranType = 'INV' AND RefNbr = '001408')
	OR (TranType = 'INV' AND RefNbr = '001409')
	OR (TranType = 'INV' AND RefNbr = '001410')
	OR (TranType = 'INV' AND RefNbr = '001411')
	OR (TranType = 'INV' AND RefNbr = '001412')
	OR (TranType = 'INV' AND RefNbr = '001413')
	OR (TranType = 'INV' AND RefNbr = '001414')
	OR (TranType = 'INV' AND RefNbr = '001415')
	OR (TranType = 'INV' AND RefNbr = '001416')
	OR (TranType = 'INV' AND RefNbr = '001417')
	OR (TranType = 'INV' AND RefNbr = '001418')
	OR (TranType = 'INV' AND RefNbr = '001419')
	OR (TranType = 'INV' AND RefNbr = '001420')
	OR (TranType = 'INV' AND RefNbr = '001421')
	OR (TranType = 'INV' AND RefNbr = '001422')
	OR (TranType = 'INV' AND RefNbr = '001423')
	OR (TranType = 'INV' AND RefNbr = '001424')
	OR (TranType = 'INV' AND RefNbr = '001425')
	OR (TranType = 'INV' AND RefNbr = '001426')
	OR (TranType = 'INV' AND RefNbr = '001427')
	OR (TranType = 'INV' AND RefNbr = '001428')
	OR (TranType = 'INV' AND RefNbr = '001429')
	OR (TranType = 'INV' AND RefNbr = '001430')
	OR (TranType = 'INV' AND RefNbr = '001431')
	OR (TranType = 'INV' AND RefNbr = '001432')
	OR (TranType = 'INV' AND RefNbr = '001433')
	OR (TranType = 'INV' AND RefNbr = '001434')
	OR (TranType = 'INV' AND RefNbr = '001435')
	OR (TranType = 'INV' AND RefNbr = '001436')
	OR (TranType = 'INV' AND RefNbr = '001437')
	OR (TranType = 'INV' AND RefNbr = '001438')
	OR (TranType = 'INV' AND RefNbr = '001439')
	OR (TranType = 'INV' AND RefNbr = '001440')
	OR (TranType = 'INV' AND RefNbr = '001441')
	OR (TranType = 'INV' AND RefNbr = '001442')
	OR (TranType = 'INV' AND RefNbr = '001443')
	OR (TranType = 'INV' AND RefNbr = '001444')
	OR (TranType = 'INV' AND RefNbr = '001445')
	OR (TranType = 'INV' AND RefNbr = '001446')
	OR (TranType = 'INV' AND RefNbr = '001447')
	OR (TranType = 'INV' AND RefNbr = '001448')
	OR (TranType = 'INV' AND RefNbr = '001449')
	OR (TranType = 'INV' AND RefNbr = '001450')
	OR (TranType = 'INV' AND RefNbr = '001451')
	OR (TranType = 'INV' AND RefNbr = '001452')
	OR (TranType = 'INV' AND RefNbr = '001453')
	OR (TranType = 'INV' AND RefNbr = '001454')
	OR (TranType = 'INV' AND RefNbr = '001455')
	OR (TranType = 'INV' AND RefNbr = '001456')
	OR (TranType = 'INV' AND RefNbr = '001457')
	OR (TranType = 'INV' AND RefNbr = '001458')
	OR (TranType = 'INV' AND RefNbr = '001459')
	OR (TranType = 'INV' AND RefNbr = '001460')
	OR (TranType = 'INV' AND RefNbr = '001461')
	OR (TranType = 'INV' AND RefNbr = '001462')
	OR (TranType = 'INV' AND RefNbr = '001463')
	OR (TranType = 'INV' AND RefNbr = '001464')
	OR (TranType = 'INV' AND RefNbr = '001465')
	OR (TranType = 'INV' AND RefNbr = '001466')
	OR (TranType = 'INV' AND RefNbr = '001467')
	OR (TranType = 'INV' AND RefNbr = '001468')
	OR (TranType = 'INV' AND RefNbr = '001469')
	OR (TranType = 'INV' AND RefNbr = '001470')
	OR (TranType = 'INV' AND RefNbr = '001471')
	OR (TranType = 'INV' AND RefNbr = '001472')
	OR (TranType = 'INV' AND RefNbr = '001473')
	OR (TranType = 'INV' AND RefNbr = '001474')
	OR (TranType = 'INV' AND RefNbr = '001475')
	OR (TranType = 'INV' AND RefNbr = '001476')
	OR (TranType = 'INV' AND RefNbr = '001477')
	OR (TranType = 'INV' AND RefNbr = '001478')
	OR (TranType = 'INV' AND RefNbr = '001479')
	OR (TranType = 'INV' AND RefNbr = '001480')
	OR (TranType = 'INV' AND RefNbr = '001481')
	OR (TranType = 'INV' AND RefNbr = '001482')
	OR (TranType = 'INV' AND RefNbr = '001483')
	OR (TranType = 'INV' AND RefNbr = '001484')
	OR (TranType = 'INV' AND RefNbr = '001485')
	OR (TranType = 'INV' AND RefNbr = '001486')
	OR (TranType = 'INV' AND RefNbr = '001487')
	OR (TranType = 'INV' AND RefNbr = '001488')
	OR (TranType = 'INV' AND RefNbr = '001489')
	OR (TranType = 'INV' AND RefNbr = '001490')
	OR (TranType = 'INV' AND RefNbr = '001491')
	OR (TranType = 'INV' AND RefNbr = '001492')
	OR (TranType = 'INV' AND RefNbr = '001493')
	OR (TranType = 'INV' AND RefNbr = '001494')
	OR (TranType = 'INV' AND RefNbr = '001495')
	OR (TranType = 'INV' AND RefNbr = '001496')
	OR (TranType = 'INV' AND RefNbr = '001497')
	OR (TranType = 'INV' AND RefNbr = '001498')
	OR (TranType = 'INV' AND RefNbr = '001499')
	OR (TranType = 'INV' AND RefNbr = '001500')
	OR (TranType = 'INV' AND RefNbr = '001501')
	OR (TranType = 'INV' AND RefNbr = '001502')
	OR (TranType = 'INV' AND RefNbr = '001503')
	OR (TranType = 'INV' AND RefNbr = '001504')
	OR (TranType = 'INV' AND RefNbr = '001505')
	OR (TranType = 'INV' AND RefNbr = '001506')
	OR (TranType = 'INV' AND RefNbr = '001507')
	OR (TranType = 'INV' AND RefNbr = '001508')
	OR (TranType = 'INV' AND RefNbr = '001509')
	OR (TranType = 'INV' AND RefNbr = '001510')
	OR (TranType = 'INV' AND RefNbr = '001511')
	OR (TranType = 'INV' AND RefNbr = '001512')
	OR (TranType = 'INV' AND RefNbr = '001513')
	OR (TranType = 'INV' AND RefNbr = '001514')
	OR (TranType = 'INV' AND RefNbr = '001515')
	OR (TranType = 'INV' AND RefNbr = '001516')
	OR (TranType = 'INV' AND RefNbr = '001517')
	OR (TranType = 'INV' AND RefNbr = '001518')
	OR (TranType = 'INV' AND RefNbr = '001519')
	OR (TranType = 'INV' AND RefNbr = '001520')
	OR (TranType = 'INV' AND RefNbr = '001521')
	OR (TranType = 'INV' AND RefNbr = '001522')
	OR (TranType = 'INV' AND RefNbr = '001523')
	OR (TranType = 'INV' AND RefNbr = '001524')
	OR (TranType = 'INV' AND RefNbr = '001525')
	OR (TranType = 'INV' AND RefNbr = '001526')
	OR (TranType = 'INV' AND RefNbr = '001527')
	OR (TranType = 'INV' AND RefNbr = '001528')
	OR (TranType = 'INV' AND RefNbr = '001529')
	OR (TranType = 'INV' AND RefNbr = '001530')
	OR (TranType = 'INV' AND RefNbr = '001531')
	OR (TranType = 'INV' AND RefNbr = '001532')
	OR (TranType = 'INV' AND RefNbr = '001533')
	OR (TranType = 'INV' AND RefNbr = '001534')
	OR (TranType = 'INV' AND RefNbr = '001535')
	OR (TranType = 'INV' AND RefNbr = '001536')
	OR (TranType = 'INV' AND RefNbr = '001537')
	OR (TranType = 'INV' AND RefNbr = '001538')
	OR (TranType = 'INV' AND RefNbr = '001539')
	OR (TranType = 'INV' AND RefNbr = '001540')
	OR (TranType = 'INV' AND RefNbr = '001541')
	OR (TranType = 'INV' AND RefNbr = '001542')
	OR (TranType = 'INV' AND RefNbr = '001543')
	OR (TranType = 'INV' AND RefNbr = '001544')
	OR (TranType = 'INV' AND RefNbr = '001545')
	OR (TranType = 'INV' AND RefNbr = '001546')
	OR (TranType = 'INV' AND RefNbr = '001547')
	OR (TranType = 'INV' AND RefNbr = '001548')
	OR (TranType = 'INV' AND RefNbr = '001549')
	OR (TranType = 'INV' AND RefNbr = '001550')
	OR (TranType = 'INV' AND RefNbr = '001551')
	OR (TranType = 'INV' AND RefNbr = '001552')
	OR (TranType = 'INV' AND RefNbr = '001553')
	OR (TranType = 'INV' AND RefNbr = '001554')
	OR (TranType = 'INV' AND RefNbr = '001555')
	OR (TranType = 'INV' AND RefNbr = '001556')
	OR (TranType = 'INV' AND RefNbr = '001557')
	OR (TranType = 'INV' AND RefNbr = '001558')
	OR (TranType = 'INV' AND RefNbr = '001559')
	OR (TranType = 'INV' AND RefNbr = '001560')
	OR (TranType = 'INV' AND RefNbr = '001561')
	OR (TranType = 'INV' AND RefNbr = '001562')
	OR (TranType = 'INV' AND RefNbr = '001563')
	OR (TranType = 'INV' AND RefNbr = '001564')
	OR (TranType = 'INV' AND RefNbr = '001565')
	OR (TranType = 'INV' AND RefNbr = '001566')
	OR (TranType = 'INV' AND RefNbr = '001567')
	OR (TranType = 'INV' AND RefNbr = '001568')
	OR (TranType = 'INV' AND RefNbr = '001569')
	OR (TranType = 'INV' AND RefNbr = '001570')
	OR (TranType = 'INV' AND RefNbr = '001571')
	OR (TranType = 'INV' AND RefNbr = '001572')
	OR (TranType = 'INV' AND RefNbr = '001573')
	OR (TranType = 'INV' AND RefNbr = '001574')
	OR (TranType = 'INV' AND RefNbr = '001575')
	OR (TranType = 'INV' AND RefNbr = '001576')
	OR (TranType = 'INV' AND RefNbr = '001579')
	OR (TranType = 'INV' AND RefNbr = '001580')
	OR (TranType = 'INV' AND RefNbr = '001581')
	OR (TranType = 'INV' AND RefNbr = '001582')
	OR (TranType = 'INV' AND RefNbr = '001583')
	OR (TranType = 'INV' AND RefNbr = '001584')
	OR (TranType = 'INV' AND RefNbr = '001585')
	OR (TranType = 'INV' AND RefNbr = '001586')
	OR (TranType = 'INV' AND RefNbr = '001587')
	OR (TranType = 'INV' AND RefNbr = '001588')
	OR (TranType = 'INV' AND RefNbr = '001589')
	OR (TranType = 'INV' AND RefNbr = '001590')
	OR (TranType = 'INV' AND RefNbr = '001591')
	OR (TranType = 'INV' AND RefNbr = '001592')
	OR (TranType = 'INV' AND RefNbr = '001593')
	OR (TranType = 'INV' AND RefNbr = '001594')
	OR (TranType = 'INV' AND RefNbr = '001595')
	OR (TranType = 'INV' AND RefNbr = '001596')
	OR (TranType = 'INV' AND RefNbr = '001597')
	OR (TranType = 'INV' AND RefNbr = '001598')
	OR (TranType = 'INV' AND RefNbr = '001599')
	OR (TranType = 'INV' AND RefNbr = '001600')
	OR (TranType = 'INV' AND RefNbr = '001601')
	OR (TranType = 'INV' AND RefNbr = '001602')
	OR (TranType = 'INV' AND RefNbr = '001603')
	OR (TranType = 'INV' AND RefNbr = '001604')
	OR (TranType = 'INV' AND RefNbr = '001605')
	OR (TranType = 'INV' AND RefNbr = '001606')
	OR (TranType = 'INV' AND RefNbr = '001607')
	OR (TranType = 'INV' AND RefNbr = '001608')
	OR (TranType = 'INV' AND RefNbr = '001609')
	OR (TranType = 'INV' AND RefNbr = '001610')
	OR (TranType = 'INV' AND RefNbr = '001611')
	OR (TranType = 'INV' AND RefNbr = '001612')
	OR (TranType = 'INV' AND RefNbr = '001613')
	OR (TranType = 'INV' AND RefNbr = '001614')
	OR (TranType = 'INV' AND RefNbr = '001615')
	OR (TranType = 'INV' AND RefNbr = '001616')
	OR (TranType = 'INV' AND RefNbr = '001617')
	OR (TranType = 'INV' AND RefNbr = '001618')
	OR (TranType = 'INV' AND RefNbr = '001619')
	OR (TranType = 'INV' AND RefNbr = '001620')
	OR (TranType = 'INV' AND RefNbr = '001621')
	OR (TranType = 'INV' AND RefNbr = '001622')
	OR (TranType = 'INV' AND RefNbr = '001623')
	OR (TranType = 'INV' AND RefNbr = '001624')
	OR (TranType = 'INV' AND RefNbr = '001625')
	OR (TranType = 'INV' AND RefNbr = '001626')
	OR (TranType = 'INV' AND RefNbr = '001627')
	OR (TranType = 'INV' AND RefNbr = '001628')
	OR (TranType = 'INV' AND RefNbr = '001629')
	OR (TranType = 'INV' AND RefNbr = '001630')
	OR (TranType = 'INV' AND RefNbr = '001631')
	OR (TranType = 'INV' AND RefNbr = '001632')
	OR (TranType = 'INV' AND RefNbr = '001633')
	OR (TranType = 'INV' AND RefNbr = '001634')
	OR (TranType = 'INV' AND RefNbr = '001635')
	OR (TranType = 'INV' AND RefNbr = '001636')
	OR (TranType = 'INV' AND RefNbr = '001637')
	OR (TranType = 'INV' AND RefNbr = '001638')
	OR (TranType = 'INV' AND RefNbr = '001639')
	OR (TranType = 'INV' AND RefNbr = '001640')
	OR (TranType = 'INV' AND RefNbr = '001641')
	OR (TranType = 'INV' AND RefNbr = '001642')
	OR (TranType = 'INV' AND RefNbr = '001643')
	OR (TranType = 'INV' AND RefNbr = '001644')
	OR (TranType = 'INV' AND RefNbr = '001645')
	OR (TranType = 'INV' AND RefNbr = '001646')
	OR (TranType = 'INV' AND RefNbr = '001647')
	OR (TranType = 'INV' AND RefNbr = '001648')
	OR (TranType = 'INV' AND RefNbr = '001649')
	OR (TranType = 'INV' AND RefNbr = '001650')
	OR (TranType = 'INV' AND RefNbr = '001651')
	OR (TranType = 'INV' AND RefNbr = '001652')
	OR (TranType = 'INV' AND RefNbr = '001653')
	OR (TranType = 'INV' AND RefNbr = '001654')
	OR (TranType = 'INV' AND RefNbr = '001655')
	OR (TranType = 'INV' AND RefNbr = '001656')
	OR (TranType = 'INV' AND RefNbr = '001657')
	OR (TranType = 'INV' AND RefNbr = '001658')
	OR (TranType = 'INV' AND RefNbr = '001659')
	OR (TranType = 'INV' AND RefNbr = '001660')
	OR (TranType = 'INV' AND RefNbr = '001661')
	OR (TranType = 'INV' AND RefNbr = '001662')
	OR (TranType = 'INV' AND RefNbr = '001663')
	OR (TranType = 'INV' AND RefNbr = '001664')
	OR (TranType = 'INV' AND RefNbr = '001665')
	OR (TranType = 'INV' AND RefNbr = '001666')
	OR (TranType = 'INV' AND RefNbr = '001667')
	OR (TranType = 'INV' AND RefNbr = '001668')
	OR (TranType = 'INV' AND RefNbr = '001669')
	OR (TranType = 'INV' AND RefNbr = '001670')
	OR (TranType = 'INV' AND RefNbr = '001671')
	OR (TranType = 'INV' AND RefNbr = '001672')
	OR (TranType = 'INV' AND RefNbr = '001673')
	OR (TranType = 'INV' AND RefNbr = '001674')
	OR (TranType = 'INV' AND RefNbr = '001675')
	OR (TranType = 'INV' AND RefNbr = '001676')
	OR (TranType = 'INV' AND RefNbr = '001677')
	OR (TranType = 'INV' AND RefNbr = '001678')
	OR (TranType = 'INV' AND RefNbr = '001679')
	OR (TranType = 'INV' AND RefNbr = '001680')
	OR (TranType = 'INV' AND RefNbr = '001681')
	OR (TranType = 'INV' AND RefNbr = '001682')
	OR (TranType = 'INV' AND RefNbr = '001683')
	OR (TranType = 'INV' AND RefNbr = '001684')
	OR (TranType = 'INV' AND RefNbr = '001685')
	OR (TranType = 'INV' AND RefNbr = '001686')
	OR (TranType = 'INV' AND RefNbr = '001687')
	OR (TranType = 'INV' AND RefNbr = '001688')
	OR (TranType = 'INV' AND RefNbr = '001689')
	OR (TranType = 'INV' AND RefNbr = '001690')
	OR (TranType = 'INV' AND RefNbr = '001691')
	OR (TranType = 'INV' AND RefNbr = '001692')
	OR (TranType = 'INV' AND RefNbr = '001693')
	OR (TranType = 'INV' AND RefNbr = '001694')
	OR (TranType = 'INV' AND RefNbr = '001695')
	OR (TranType = 'INV' AND RefNbr = '001696')
	OR (TranType = 'INV' AND RefNbr = '001697')
	OR (TranType = 'INV' AND RefNbr = '001698')
	OR (TranType = 'INV' AND RefNbr = '001699')
	OR (TranType = 'INV' AND RefNbr = '001700')
	OR (TranType = 'INV' AND RefNbr = '001701')
	OR (TranType = 'INV' AND RefNbr = '001702')
	OR (TranType = 'INV' AND RefNbr = '001703')
	OR (TranType = 'INV' AND RefNbr = '001704')
	OR (TranType = 'INV' AND RefNbr = '001705')
	OR (TranType = 'INV' AND RefNbr = '001706')
	OR (TranType = 'INV' AND RefNbr = '001707')
	OR (TranType = 'INV' AND RefNbr = '001708')
	OR (TranType = 'INV' AND RefNbr = '001709')
	OR (TranType = 'INV' AND RefNbr = '001710')
	OR (TranType = 'INV' AND RefNbr = '001711')
	OR (TranType = 'INV' AND RefNbr = '001712')
	OR (TranType = 'INV' AND RefNbr = '001713')
	OR (TranType = 'INV' AND RefNbr = '001714')
	OR (TranType = 'INV' AND RefNbr = '001715')
	OR (TranType = 'INV' AND RefNbr = '001716')
	OR (TranType = 'INV' AND RefNbr = '001717')
	OR (TranType = 'INV' AND RefNbr = '001718')
	OR (TranType = 'INV' AND RefNbr = '001719')
	OR (TranType = 'INV' AND RefNbr = '001720')
	OR (TranType = 'INV' AND RefNbr = '001721')
	OR (TranType = 'INV' AND RefNbr = '001722')
	OR (TranType = 'INV' AND RefNbr = '001723')
	OR (TranType = 'INV' AND RefNbr = '001724')
	OR (TranType = 'INV' AND RefNbr = '001725')
	OR (TranType = 'INV' AND RefNbr = '001726')
	OR (TranType = 'INV' AND RefNbr = '001727')
	OR (TranType = 'INV' AND RefNbr = '001728')
	OR (TranType = 'INV' AND RefNbr = '001729')
	OR (TranType = 'INV' AND RefNbr = '001730')
	OR (TranType = 'INV' AND RefNbr = '001731')
	OR (TranType = 'INV' AND RefNbr = '001732')
	OR (TranType = 'INV' AND RefNbr = '001733')
	OR (TranType = 'INV' AND RefNbr = '001734')
	OR (TranType = 'INV' AND RefNbr = '001735')
	OR (TranType = 'INV' AND RefNbr = '001736')
	OR (TranType = 'INV' AND RefNbr = '001737')
	OR (TranType = 'INV' AND RefNbr = '001738')
	OR (TranType = 'INV' AND RefNbr = '001739')
	OR (TranType = 'INV' AND RefNbr = '001740')
	OR (TranType = 'INV' AND RefNbr = '001741')
	OR (TranType = 'INV' AND RefNbr = '001742')
	OR (TranType = 'INV' AND RefNbr = '001743')
	OR (TranType = 'INV' AND RefNbr = '001744')
	OR (TranType = 'INV' AND RefNbr = '001745')
	OR (TranType = 'INV' AND RefNbr = '001746')
	OR (TranType = 'INV' AND RefNbr = '001747')
	OR (TranType = 'INV' AND RefNbr = '001748')
	OR (TranType = 'INV' AND RefNbr = '001749')
	OR (TranType = 'INV' AND RefNbr = '001750')
	OR (TranType = 'INV' AND RefNbr = '001751')
	OR (TranType = 'INV' AND RefNbr = '001752')
	OR (TranType = 'INV' AND RefNbr = '001753')
	OR (TranType = 'INV' AND RefNbr = '001754')
	OR (TranType = 'INV' AND RefNbr = '001755')
	OR (TranType = 'INV' AND RefNbr = '001756')
	OR (TranType = 'INV' AND RefNbr = '001757')
	OR (TranType = 'INV' AND RefNbr = '001758')
	OR (TranType = 'INV' AND RefNbr = '001759')
	OR (TranType = 'INV' AND RefNbr = '001760')
	OR (TranType = 'INV' AND RefNbr = '001761')
	OR (TranType = 'INV' AND RefNbr = '001762')
	OR (TranType = 'INV' AND RefNbr = '001763')
	OR (TranType = 'INV' AND RefNbr = '001764')
	OR (TranType = 'INV' AND RefNbr = '001765')
	OR (TranType = 'INV' AND RefNbr = '001766')
	OR (TranType = 'INV' AND RefNbr = '001767')
	OR (TranType = 'INV' AND RefNbr = '001768')
	OR (TranType = 'INV' AND RefNbr = '001769')
	OR (TranType = 'INV' AND RefNbr = '001770')
	OR (TranType = 'INV' AND RefNbr = '001771')
	OR (TranType = 'INV' AND RefNbr = '001772')
	OR (TranType = 'INV' AND RefNbr = '001773')
	OR (TranType = 'INV' AND RefNbr = '001774')
	OR (TranType = 'INV' AND RefNbr = '001775')
	OR (TranType = 'INV' AND RefNbr = '001776')
	OR (TranType = 'INV' AND RefNbr = '001777')
	OR (TranType = 'INV' AND RefNbr = '001778')
	OR (TranType = 'INV' AND RefNbr = '001779')
	OR (TranType = 'INV' AND RefNbr = '001780')
	OR (TranType = 'INV' AND RefNbr = '001781')
	OR (TranType = 'INV' AND RefNbr = '001782')
	OR (TranType = 'INV' AND RefNbr = '001783')
	OR (TranType = 'INV' AND RefNbr = '001784')
	OR (TranType = 'INV' AND RefNbr = '001785')
	OR (TranType = 'INV' AND RefNbr = '001786')
	OR (TranType = 'INV' AND RefNbr = '001787')
	OR (TranType = 'INV' AND RefNbr = '001788')
	OR (TranType = 'INV' AND RefNbr = '001789')
	OR (TranType = 'INV' AND RefNbr = '001790')
	OR (TranType = 'INV' AND RefNbr = '001791')
	OR (TranType = 'INV' AND RefNbr = '001792')
	OR (TranType = 'INV' AND RefNbr = '001793')
	OR (TranType = 'INV' AND RefNbr = '001794')
	OR (TranType = 'INV' AND RefNbr = '001795')
	OR (TranType = 'INV' AND RefNbr = '001796')
	OR (TranType = 'INV' AND RefNbr = '001801')
	OR (TranType = 'INV' AND RefNbr = '001802')
	OR (TranType = 'INV' AND RefNbr = '001803')
	OR (TranType = 'INV' AND RefNbr = '001804')
	OR (TranType = 'INV' AND RefNbr = '001805')
	OR (TranType = 'INV' AND RefNbr = '001806')
	OR (TranType = 'INV' AND RefNbr = '001807')
	OR (TranType = 'INV' AND RefNbr = '001808')
	OR (TranType = 'INV' AND RefNbr = '001809')
	OR (TranType = 'INV' AND RefNbr = '001813')
	OR (TranType = 'INV' AND RefNbr = '001814')
	OR (TranType = 'INV' AND RefNbr = '001815')
	OR (TranType = 'INV' AND RefNbr = '001816')
	OR (TranType = 'INV' AND RefNbr = '001817')
	OR (TranType = 'INV' AND RefNbr = '001818')
	OR (TranType = 'INV' AND RefNbr = '001819')
	OR (TranType = 'INV' AND RefNbr = '001820')
	OR (TranType = 'INV' AND RefNbr = '001821')
	OR (TranType = 'INV' AND RefNbr = '001825')
	OR (TranType = 'INV' AND RefNbr = '001826')
	OR (TranType = 'INV' AND RefNbr = '001827')
	OR (TranType = 'INV' AND RefNbr = '001828')
	OR (TranType = 'INV' AND RefNbr = '001829')
	OR (TranType = 'INV' AND RefNbr = '001830')
	OR (TranType = 'INV' AND RefNbr = '001831')
	OR (TranType = 'INV' AND RefNbr = '001832')
	OR (TranType = 'INV' AND RefNbr = '001833')
	OR (TranType = 'INV' AND RefNbr = '001836')
	OR (TranType = 'INV' AND RefNbr = '001840')
	OR (TranType = 'INV' AND RefNbr = '001841')
	OR (TranType = 'INV' AND RefNbr = '001842')
	OR (TranType = 'INV' AND RefNbr = '001843')
	OR (TranType = 'INV' AND RefNbr = '001844')
	OR (TranType = 'INV' AND RefNbr = '001845')
	OR (TranType = 'INV' AND RefNbr = '001846')
	OR (TranType = 'INV' AND RefNbr = '001847')
	OR (TranType = 'INV' AND RefNbr = '001848')
	OR (TranType = 'INV' AND RefNbr = '001852')
	OR (TranType = 'INV' AND RefNbr = '001853')
	OR (TranType = 'INV' AND RefNbr = '001854')
	OR (TranType = 'INV' AND RefNbr = '001855')
	OR (TranType = 'INV' AND RefNbr = '001856')
	OR (TranType = 'INV' AND RefNbr = '001857')
	OR (TranType = 'INV' AND RefNbr = '001858')
	OR (TranType = 'INV' AND RefNbr = '001859')
	OR (TranType = 'INV' AND RefNbr = '001860')
	OR (TranType = 'INV' AND RefNbr = '001862')
	OR (TranType = 'INV' AND RefNbr = '001863')
	OR (TranType = 'INV' AND RefNbr = '001864')
	OR (TranType = 'INV' AND RefNbr = '001866')
	OR (TranType = 'INV' AND RefNbr = '001867')
	OR (TranType = 'INV' AND RefNbr = '001868')
	OR (TranType = 'INV' AND RefNbr = '001871')
	OR (TranType = 'INV' AND RefNbr = '001872')
	OR (TranType = 'INV' AND RefNbr = '001873')
	OR (TranType = 'INV' AND RefNbr = '001874')
	OR (TranType = 'INV' AND RefNbr = '001875')
	OR (TranType = 'INV' AND RefNbr = '001876')
	OR (TranType = 'INV' AND RefNbr = '001880')
	OR (TranType = 'INV' AND RefNbr = '001881')
	OR (TranType = 'INV' AND RefNbr = '001882')
	OR (TranType = 'INV' AND RefNbr = '001883')
	OR (TranType = 'INV' AND RefNbr = '001884')
	OR (TranType = 'INV' AND RefNbr = '001885')
	OR (TranType = 'INV' AND RefNbr = '001886')
	OR (TranType = 'INV' AND RefNbr = '001887')
	OR (TranType = 'INV' AND RefNbr = '001888')
	OR (TranType = 'INV' AND RefNbr = '001892')
	OR (TranType = 'INV' AND RefNbr = '001893')
	OR (TranType = 'INV' AND RefNbr = '001894')
	OR (TranType = 'INV' AND RefNbr = '001895')
	OR (TranType = 'INV' AND RefNbr = '001896')
	OR (TranType = 'INV' AND RefNbr = '001897')
	OR (TranType = 'INV' AND RefNbr = '001898')
	OR (TranType = 'INV' AND RefNbr = '001899')
	OR (TranType = 'INV' AND RefNbr = '001900')
	OR (TranType = 'INV' AND RefNbr = '001901')
	OR (TranType = 'INV' AND RefNbr = '001902')
	OR (TranType = 'INV' AND RefNbr = '001903')
	OR (TranType = 'INV' AND RefNbr = '001904')
	OR (TranType = 'INV' AND RefNbr = '001905')
	OR (TranType = 'INV' AND RefNbr = '001906')
	OR (TranType = 'INV' AND RefNbr = '001907')
	OR (TranType = 'INV' AND RefNbr = '001908')
	OR (TranType = 'INV' AND RefNbr = '001909')
	OR (TranType = 'INV' AND RefNbr = '001910')
	OR (TranType = 'INV' AND RefNbr = '001911')
	OR (TranType = 'INV' AND RefNbr = '001912')
	OR (TranType = 'INV' AND RefNbr = '001913')
	OR (TranType = 'INV' AND RefNbr = '001914')
	OR (TranType = 'INV' AND RefNbr = '001915')
	OR (TranType = 'INV' AND RefNbr = '001916')
	OR (TranType = 'INV' AND RefNbr = '001917')
	OR (TranType = 'INV' AND RefNbr = '001918')
	OR (TranType = 'INV' AND RefNbr = '001919')
	OR (TranType = 'INV' AND RefNbr = '001920')
	OR (TranType = 'INV' AND RefNbr = '001921')
	OR (TranType = 'INV' AND RefNbr = '001922')
	OR (TranType = 'INV' AND RefNbr = '001923')
	OR (TranType = 'INV' AND RefNbr = '001924')
	OR (TranType = 'INV' AND RefNbr = '001925')
	OR (TranType = 'INV' AND RefNbr = '001926')
	OR (TranType = 'INV' AND RefNbr = '001927')
	OR (TranType = 'INV' AND RefNbr = '001928')
	OR (TranType = 'INV' AND RefNbr = '001929')
	OR (TranType = 'INV' AND RefNbr = '001930')
	OR (TranType = 'INV' AND RefNbr = '001931')
	OR (TranType = 'INV' AND RefNbr = '001932')
	OR (TranType = 'INV' AND RefNbr = '001933')
	OR (TranType = 'INV' AND RefNbr = '001934')
	OR (TranType = 'INV' AND RefNbr = '001935')
	OR (TranType = 'INV' AND RefNbr = '001936')
	OR (TranType = 'INV' AND RefNbr = '001937')
	OR (TranType = 'INV' AND RefNbr = '001938')
	OR (TranType = 'INV' AND RefNbr = '001939')
	OR (TranType = 'INV' AND RefNbr = '001940')
	OR (TranType = 'INV' AND RefNbr = '001941')
	OR (TranType = 'INV' AND RefNbr = '001942')
	OR (TranType = 'INV' AND RefNbr = '001943')
	OR (TranType = 'INV' AND RefNbr = '001944')
	OR (TranType = 'INV' AND RefNbr = '001945')
	OR (TranType = 'INV' AND RefNbr = '001946')
	OR (TranType = 'INV' AND RefNbr = '001947')
	OR (TranType = 'INV' AND RefNbr = '001948')
	OR (TranType = 'INV' AND RefNbr = '001949')
	OR (TranType = 'INV' AND RefNbr = '001950')
	OR (TranType = 'INV' AND RefNbr = '001951')
	OR (TranType = 'INV' AND RefNbr = '001952')
	OR (TranType = 'INV' AND RefNbr = '001953')
	OR (TranType = 'INV' AND RefNbr = '001954')
	OR (TranType = 'INV' AND RefNbr = '001955')
	OR (TranType = 'INV' AND RefNbr = '001956')
	OR (TranType = 'INV' AND RefNbr = '001957')
	OR (TranType = 'INV' AND RefNbr = '001958')
	OR (TranType = 'INV' AND RefNbr = '001959')
	OR (TranType = 'INV' AND RefNbr = '001960')
	OR (TranType = 'INV' AND RefNbr = '001961')
	OR (TranType = 'INV' AND RefNbr = '001962')
	OR (TranType = 'INV' AND RefNbr = '001963')
	OR (TranType = 'INV' AND RefNbr = '001964')
	OR (TranType = 'INV' AND RefNbr = '001965')
	OR (TranType = 'INV' AND RefNbr = '001966')
	OR (TranType = 'INV' AND RefNbr = '001967')
	OR (TranType = 'INV' AND RefNbr = '001968')
	OR (TranType = 'INV' AND RefNbr = '001969')
	OR (TranType = 'INV' AND RefNbr = '001970')
	OR (TranType = 'INV' AND RefNbr = '001971')
	OR (TranType = 'INV' AND RefNbr = '001972')
	OR (TranType = 'INV' AND RefNbr = '001973')
	OR (TranType = 'INV' AND RefNbr = '001974')
	OR (TranType = 'INV' AND RefNbr = '001975')
	OR (TranType = 'INV' AND RefNbr = '001976')
	OR (TranType = 'INV' AND RefNbr = '001977')
	OR (TranType = 'INV' AND RefNbr = '001978')
	OR (TranType = 'INV' AND RefNbr = '001979')
	OR (TranType = 'INV' AND RefNbr = '001980')
	OR (TranType = 'INV' AND RefNbr = '001981')
	OR (TranType = 'INV' AND RefNbr = '001982')
	OR (TranType = 'INV' AND RefNbr = '001983')
	OR (TranType = 'INV' AND RefNbr = '001984')
	OR (TranType = 'INV' AND RefNbr = '001985')
	OR (TranType = 'INV' AND RefNbr = '001986')
	OR (TranType = 'INV' AND RefNbr = '001987')
	OR (TranType = 'INV' AND RefNbr = '001988')
	OR (TranType = 'INV' AND RefNbr = '001989')
	OR (TranType = 'INV' AND RefNbr = '001990')
	OR (TranType = 'INV' AND RefNbr = '001991')
	OR (TranType = 'INV' AND RefNbr = '001992')
	OR (TranType = 'INV' AND RefNbr = '001993')
	OR (TranType = 'INV' AND RefNbr = '001994')
	OR (TranType = 'INV' AND RefNbr = '001995')
	OR (TranType = 'INV' AND RefNbr = '001996')
	OR (TranType = 'INV' AND RefNbr = '001997')
	OR (TranType = 'INV' AND RefNbr = '001998')
	OR (TranType = 'INV' AND RefNbr = '001999')
	OR (TranType = 'INV' AND RefNbr = '002000')
	OR (TranType = 'INV' AND RefNbr = '002002')
	OR (TranType = 'INV' AND RefNbr = '002003')
	OR (TranType = 'INV' AND RefNbr = '002004')
	OR (TranType = 'INV' AND RefNbr = '002005')
	OR (TranType = 'INV' AND RefNbr = '002006')
	OR (TranType = 'INV' AND RefNbr = '002007')
	OR (TranType = 'INV' AND RefNbr = '002008')
	OR (TranType = 'INV' AND RefNbr = '002009')
	OR (TranType = 'INV' AND RefNbr = '002010')
	OR (TranType = 'INV' AND RefNbr = '002011')
	OR (TranType = 'INV' AND RefNbr = '002012')
	OR (TranType = 'INV' AND RefNbr = '002013')
	OR (TranType = 'INV' AND RefNbr = '002014')
	OR (TranType = 'INV' AND RefNbr = '002015')
	OR (TranType = 'INV' AND RefNbr = '002016')
	OR (TranType = 'INV' AND RefNbr = '002017')
	OR (TranType = 'INV' AND RefNbr = '002018')
	OR (TranType = 'INV' AND RefNbr = '002019')
	OR (TranType = 'INV' AND RefNbr = '002020')
	OR (TranType = 'INV' AND RefNbr = '002021')
	OR (TranType = 'INV' AND RefNbr = '002022')
	OR (TranType = 'INV' AND RefNbr = '002023')
	OR (TranType = 'INV' AND RefNbr = '002024')
	OR (TranType = 'INV' AND RefNbr = '002025')
	OR (TranType = 'INV' AND RefNbr = '002026')
	OR (TranType = 'INV' AND RefNbr = '002027')
	OR (TranType = 'INV' AND RefNbr = '002028')
	OR (TranType = 'INV' AND RefNbr = '002029')
	OR (TranType = 'INV' AND RefNbr = '002030')
	OR (TranType = 'INV' AND RefNbr = '002031')
	OR (TranType = 'INV' AND RefNbr = '002032')
	OR (TranType = 'INV' AND RefNbr = '002033')
	OR (TranType = 'INV' AND RefNbr = '002034')
	OR (TranType = 'INV' AND RefNbr = '002035')
	OR (TranType = 'INV' AND RefNbr = '002036')
	OR (TranType = 'INV' AND RefNbr = '002037')
	OR (TranType = 'INV' AND RefNbr = '002038')
	OR (TranType = 'INV' AND RefNbr = '002039')
	OR (TranType = 'INV' AND RefNbr = '002040')
	OR (TranType = 'INV' AND RefNbr = '002041')
	OR (TranType = 'INV' AND RefNbr = '002042')
	OR (TranType = 'INV' AND RefNbr = '002044')
	OR (TranType = 'INV' AND RefNbr = '002045')
	OR (TranType = 'INV' AND RefNbr = '002046')
	OR (TranType = 'INV' AND RefNbr = '002047')
	OR (TranType = 'INV' AND RefNbr = '002048')
	OR (TranType = 'INV' AND RefNbr = '002049')
	OR (TranType = 'INV' AND RefNbr = '002050')
	OR (TranType = 'INV' AND RefNbr = '002051')
	OR (TranType = 'INV' AND RefNbr = '002052')
	OR (TranType = 'INV' AND RefNbr = '002053')
	OR (TranType = 'INV' AND RefNbr = '002054')
	OR (TranType = 'INV' AND RefNbr = '002055')
	OR (TranType = 'INV' AND RefNbr = '002056')
	OR (TranType = 'INV' AND RefNbr = '002057')
	OR (TranType = 'INV' AND RefNbr = '002058')
	OR (TranType = 'INV' AND RefNbr = '002059')
	OR (TranType = 'INV' AND RefNbr = '002060')
	OR (TranType = 'INV' AND RefNbr = '002061')
	OR (TranType = 'INV' AND RefNbr = '002062')
	OR (TranType = 'INV' AND RefNbr = '002063')
	OR (TranType = 'INV' AND RefNbr = '002064')
	OR (TranType = 'INV' AND RefNbr = '002065')
	OR (TranType = 'INV' AND RefNbr = '002066')
	OR (TranType = 'INV' AND RefNbr = '002067')
	OR (TranType = 'INV' AND RefNbr = '002068')
	OR (TranType = 'INV' AND RefNbr = '002069')
	OR (TranType = 'INV' AND RefNbr = '002070')
	OR (TranType = 'INV' AND RefNbr = '002071')
	OR (TranType = 'INV' AND RefNbr = '002072')
	OR (TranType = 'INV' AND RefNbr = '002073')
	OR (TranType = 'INV' AND RefNbr = '002074')
	OR (TranType = 'INV' AND RefNbr = '002075')
	OR (TranType = 'INV' AND RefNbr = '002076')
	OR (TranType = 'INV' AND RefNbr = '002077')
	OR (TranType = 'INV' AND RefNbr = '002078')
	OR (TranType = 'INV' AND RefNbr = '002079')
	OR (TranType = 'INV' AND RefNbr = '002080')
	OR (TranType = 'INV' AND RefNbr = '002081')
	OR (TranType = 'INV' AND RefNbr = '002082')
	OR (TranType = 'INV' AND RefNbr = '002083')
	OR (TranType = 'INV' AND RefNbr = '002084')
	OR (TranType = 'INV' AND RefNbr = '002085')
	OR (TranType = 'INV' AND RefNbr = '002086')
	OR (TranType = 'INV' AND RefNbr = '002087')
	OR (TranType = 'INV' AND RefNbr = '002088')
	OR (TranType = 'INV' AND RefNbr = '002089')
	OR (TranType = 'INV' AND RefNbr = '002090')
	OR (TranType = 'INV' AND RefNbr = '002091')
	OR (TranType = 'INV' AND RefNbr = '002092')
	OR (TranType = 'INV' AND RefNbr = '002093')
	OR (TranType = 'INV' AND RefNbr = '002094')
	OR (TranType = 'INV' AND RefNbr = '002095')
	OR (TranType = 'INV' AND RefNbr = '002096')
	OR (TranType = 'INV' AND RefNbr = '002097')
	OR (TranType = 'INV' AND RefNbr = '002098')
	OR (TranType = 'INV' AND RefNbr = '002099')
	OR (TranType = 'INV' AND RefNbr = '002100')
	OR (TranType = 'INV' AND RefNbr = '002101')
	OR (TranType = 'INV' AND RefNbr = '002102')
	OR (TranType = 'INV' AND RefNbr = '002103')
	OR (TranType = 'INV' AND RefNbr = '002104')
	OR (TranType = 'INV' AND RefNbr = '002105')
	OR (TranType = 'INV' AND RefNbr = '002106')
	OR (TranType = 'INV' AND RefNbr = '002107')
	OR (TranType = 'INV' AND RefNbr = '002108')
	OR (TranType = 'INV' AND RefNbr = '002109')
	OR (TranType = 'INV' AND RefNbr = '002110')
	OR (TranType = 'INV' AND RefNbr = '002111')
	OR (TranType = 'INV' AND RefNbr = '002112')
	OR (TranType = 'INV' AND RefNbr = '002113')
	OR (TranType = 'INV' AND RefNbr = '002114')
	OR (TranType = 'INV' AND RefNbr = '002115')
	OR (TranType = 'INV' AND RefNbr = '002116')
	OR (TranType = 'INV' AND RefNbr = '002117')
	OR (TranType = 'INV' AND RefNbr = '002118')
	OR (TranType = 'INV' AND RefNbr = '002119')
	OR (TranType = 'INV' AND RefNbr = '002120')
	OR (TranType = 'INV' AND RefNbr = '002121')
	OR (TranType = 'INV' AND RefNbr = '002122')
	OR (TranType = 'INV' AND RefNbr = '002123')
	OR (TranType = 'INV' AND RefNbr = '002124')
	OR (TranType = 'INV' AND RefNbr = '002125')
	OR (TranType = 'INV' AND RefNbr = '002126')
	OR (TranType = 'INV' AND RefNbr = '002127')
	OR (TranType = 'INV' AND RefNbr = '002128')
	OR (TranType = 'INV' AND RefNbr = '002129')
	OR (TranType = 'INV' AND RefNbr = '002130')
	OR (TranType = 'INV' AND RefNbr = '002131')
	OR (TranType = 'INV' AND RefNbr = '002132')
	OR (TranType = 'INV' AND RefNbr = '002133')
	OR (TranType = 'INV' AND RefNbr = '002134')
	OR (TranType = 'INV' AND RefNbr = '002135')
	OR (TranType = 'INV' AND RefNbr = '002136')
	OR (TranType = 'INV' AND RefNbr = '002137')
	OR (TranType = 'INV' AND RefNbr = '002138')
	OR (TranType = 'INV' AND RefNbr = '002139')
	OR (TranType = 'INV' AND RefNbr = '002140')
	OR (TranType = 'INV' AND RefNbr = '002141')
	OR (TranType = 'INV' AND RefNbr = '002142')
	OR (TranType = 'INV' AND RefNbr = '002143')
	OR (TranType = 'INV' AND RefNbr = '002144')
	OR (TranType = 'INV' AND RefNbr = '002145')
	OR (TranType = 'INV' AND RefNbr = '002146')
	OR (TranType = 'INV' AND RefNbr = '002147')
	OR (TranType = 'INV' AND RefNbr = '002148')
	OR (TranType = 'INV' AND RefNbr = '002149')
	OR (TranType = 'INV' AND RefNbr = '002150')
	OR (TranType = 'INV' AND RefNbr = '002151')
	OR (TranType = 'INV' AND RefNbr = '002152')
	OR (TranType = 'INV' AND RefNbr = '002153')
	OR (TranType = 'INV' AND RefNbr = '002154')
	OR (TranType = 'INV' AND RefNbr = '002155')
	OR (TranType = 'INV' AND RefNbr = '002156')
	OR (TranType = 'INV' AND RefNbr = '002157')
	OR (TranType = 'INV' AND RefNbr = '002158')
	OR (TranType = 'INV' AND RefNbr = '002159')
	OR (TranType = 'INV' AND RefNbr = '002160')
	OR (TranType = 'INV' AND RefNbr = '002161')
	OR (TranType = 'INV' AND RefNbr = '002162')
	OR (TranType = 'INV' AND RefNbr = '002163')
	OR (TranType = 'INV' AND RefNbr = '002164')
	OR (TranType = 'INV' AND RefNbr = '002165')
	OR (TranType = 'INV' AND RefNbr = '002166')
	OR (TranType = 'INV' AND RefNbr = '002167')
	OR (TranType = 'INV' AND RefNbr = '002168')
	OR (TranType = 'INV' AND RefNbr = '002169')
	OR (TranType = 'INV' AND RefNbr = '002170')
	OR (TranType = 'INV' AND RefNbr = '002171')
	OR (TranType = 'INV' AND RefNbr = '002172')
	OR (TranType = 'INV' AND RefNbr = '002173')
	OR (TranType = 'INV' AND RefNbr = '002174')
	OR (TranType = 'INV' AND RefNbr = '002175')
	OR (TranType = 'INV' AND RefNbr = '002176')
	OR (TranType = 'INV' AND RefNbr = '002177')
	OR (TranType = 'INV' AND RefNbr = '002178')
	OR (TranType = 'INV' AND RefNbr = '002179')
	OR (TranType = 'INV' AND RefNbr = '002180')
	OR (TranType = 'INV' AND RefNbr = '002181')
	OR (TranType = 'INV' AND RefNbr = '002182')
	OR (TranType = 'INV' AND RefNbr = '002183')
	OR (TranType = 'INV' AND RefNbr = '002184')
	OR (TranType = 'INV' AND RefNbr = '002185')
	OR (TranType = 'INV' AND RefNbr = '002186')
	OR (TranType = 'INV' AND RefNbr = '002187')
	OR (TranType = 'INV' AND RefNbr = '002188')
	OR (TranType = 'INV' AND RefNbr = '002189')
	OR (TranType = 'INV' AND RefNbr = '002190')
	OR (TranType = 'INV' AND RefNbr = '002191')
	OR (TranType = 'INV' AND RefNbr = '002192')
	OR (TranType = 'INV' AND RefNbr = '002193')
	OR (TranType = 'INV' AND RefNbr = '002194')
	OR (TranType = 'INV' AND RefNbr = '002195')
	OR (TranType = 'INV' AND RefNbr = '002196')
	OR (TranType = 'INV' AND RefNbr = '002197')
	OR (TranType = 'INV' AND RefNbr = '002198')
	OR (TranType = 'INV' AND RefNbr = '002199')
	OR (TranType = 'INV' AND RefNbr = '002200')
	OR (TranType = 'INV' AND RefNbr = '002201')
	OR (TranType = 'INV' AND RefNbr = '002202')
	OR (TranType = 'INV' AND RefNbr = '002203')
	OR (TranType = 'INV' AND RefNbr = '002204')
	OR (TranType = 'INV' AND RefNbr = '002205')
	OR (TranType = 'INV' AND RefNbr = '002206')
	OR (TranType = 'INV' AND RefNbr = '002207')
	OR (TranType = 'INV' AND RefNbr = '002208')
	OR (TranType = 'INV' AND RefNbr = '002209')
	OR (TranType = 'INV' AND RefNbr = '002210')
	OR (TranType = 'INV' AND RefNbr = '002211')
	OR (TranType = 'INV' AND RefNbr = '002212')
	OR (TranType = 'INV' AND RefNbr = '002213')
	OR (TranType = 'INV' AND RefNbr = '002214')
	OR (TranType = 'INV' AND RefNbr = '002215')
	OR (TranType = 'INV' AND RefNbr = '002216')
	OR (TranType = 'INV' AND RefNbr = '002217')
	OR (TranType = 'INV' AND RefNbr = '002218')
	OR (TranType = 'INV' AND RefNbr = '002219')
	OR (TranType = 'INV' AND RefNbr = '002220')
	OR (TranType = 'INV' AND RefNbr = '002221')
	OR (TranType = 'INV' AND RefNbr = '002222')
	OR (TranType = 'INV' AND RefNbr = '002223')
	OR (TranType = 'INV' AND RefNbr = '002224')
	OR (TranType = 'INV' AND RefNbr = '002225')
	OR (TranType = 'INV' AND RefNbr = '002226')
	OR (TranType = 'INV' AND RefNbr = '002227')
	OR (TranType = 'INV' AND RefNbr = '002228')
	OR (TranType = 'INV' AND RefNbr = '002229')
	OR (TranType = 'INV' AND RefNbr = '002230')
	OR (TranType = 'INV' AND RefNbr = '002231')
	OR (TranType = 'INV' AND RefNbr = '002232')
	OR (TranType = 'INV' AND RefNbr = '002233')
	OR (TranType = 'INV' AND RefNbr = '002234')
	OR (TranType = 'INV' AND RefNbr = '002235')
	OR (TranType = 'INV' AND RefNbr = '002236')
	OR (TranType = 'INV' AND RefNbr = '002237')
	OR (TranType = 'INV' AND RefNbr = '002238')
	OR (TranType = 'INV' AND RefNbr = '002239')
	OR (TranType = 'INV' AND RefNbr = '002240')
	OR (TranType = 'INV' AND RefNbr = '002241')
	OR (TranType = 'INV' AND RefNbr = '002242')
	OR (TranType = 'INV' AND RefNbr = '002243')
	OR (TranType = 'INV' AND RefNbr = '002244')
	OR (TranType = 'INV' AND RefNbr = '002245')
	OR (TranType = 'INV' AND RefNbr = '002246')
	OR (TranType = 'INV' AND RefNbr = '002247')
	OR (TranType = 'INV' AND RefNbr = '002248')
	OR (TranType = 'INV' AND RefNbr = '002249')
	OR (TranType = 'INV' AND RefNbr = '002250')
	OR (TranType = 'INV' AND RefNbr = '002251')
	OR (TranType = 'INV' AND RefNbr = '002252')
	OR (TranType = 'INV' AND RefNbr = '002253')
	OR (TranType = 'INV' AND RefNbr = '002254')
	OR (TranType = 'INV' AND RefNbr = '002255')
	OR (TranType = 'INV' AND RefNbr = '002256')
	OR (TranType = 'INV' AND RefNbr = '002257')
	OR (TranType = 'INV' AND RefNbr = '002258')
	OR (TranType = 'INV' AND RefNbr = '002259')
	OR (TranType = 'INV' AND RefNbr = '002260')
	OR (TranType = 'INV' AND RefNbr = '002261')
	OR (TranType = 'INV' AND RefNbr = '002262')
	OR (TranType = 'INV' AND RefNbr = '002263')
	OR (TranType = 'INV' AND RefNbr = '002264')
	OR (TranType = 'INV' AND RefNbr = '002265')
	OR (TranType = 'INV' AND RefNbr = '002266')
	OR (TranType = 'INV' AND RefNbr = '002267')
	OR (TranType = 'INV' AND RefNbr = '002268')
	OR (TranType = 'INV' AND RefNbr = '002269')
	OR (TranType = 'INV' AND RefNbr = '002270')
	OR (TranType = 'INV' AND RefNbr = '002271')
	OR (TranType = 'INV' AND RefNbr = '002272')
	OR (TranType = 'INV' AND RefNbr = '002273')
	OR (TranType = 'INV' AND RefNbr = '002274')
	OR (TranType = 'INV' AND RefNbr = '002275')
	OR (TranType = 'INV' AND RefNbr = '002276')
	OR (TranType = 'INV' AND RefNbr = '002277')
	OR (TranType = 'INV' AND RefNbr = '002278')
	OR (TranType = 'INV' AND RefNbr = '002279')
	OR (TranType = 'INV' AND RefNbr = '002280')
	OR (TranType = 'INV' AND RefNbr = '002281')
	OR (TranType = 'INV' AND RefNbr = '002282')
	OR (TranType = 'INV' AND RefNbr = '002283')
	OR (TranType = 'INV' AND RefNbr = '002284')
	OR (TranType = 'INV' AND RefNbr = '002285')
	OR (TranType = 'INV' AND RefNbr = '002286')
	OR (TranType = 'INV' AND RefNbr = '002287')
	OR (TranType = 'INV' AND RefNbr = '002288')
	OR (TranType = 'INV' AND RefNbr = '002289')
	OR (TranType = 'INV' AND RefNbr = '002290')
	OR (TranType = 'INV' AND RefNbr = '002291')
	OR (TranType = 'INV' AND RefNbr = '002292')
	OR (TranType = 'INV' AND RefNbr = '002293')
	OR (TranType = 'INV' AND RefNbr = '002294')
	OR (TranType = 'INV' AND RefNbr = '002295')
	OR (TranType = 'INV' AND RefNbr = '002296')
	OR (TranType = 'INV' AND RefNbr = '002297')
	OR (TranType = 'INV' AND RefNbr = '002298')
	OR (TranType = 'INV' AND RefNbr = '002299')
	OR (TranType = 'INV' AND RefNbr = '002300')
	OR (TranType = 'INV' AND RefNbr = '002301')
	OR (TranType = 'INV' AND RefNbr = '002302')
	OR (TranType = 'INV' AND RefNbr = '002303')
	OR (TranType = 'INV' AND RefNbr = '002304')
	OR (TranType = 'INV' AND RefNbr = '002305')
	OR (TranType = 'INV' AND RefNbr = '002306')
	OR (TranType = 'INV' AND RefNbr = '002307')
	OR (TranType = 'INV' AND RefNbr = '002308')
	OR (TranType = 'INV' AND RefNbr = '002309')
	OR (TranType = 'INV' AND RefNbr = '002310')
	OR (TranType = 'INV' AND RefNbr = '002311')
	OR (TranType = 'INV' AND RefNbr = '002312')
	OR (TranType = 'INV' AND RefNbr = '002313')
	OR (TranType = 'INV' AND RefNbr = '002314')
	OR (TranType = 'INV' AND RefNbr = '002315')
	OR (TranType = 'INV' AND RefNbr = '002316')
	OR (TranType = 'INV' AND RefNbr = '002317')
	OR (TranType = 'INV' AND RefNbr = '002318')
	OR (TranType = 'INV' AND RefNbr = '002319')
	OR (TranType = 'INV' AND RefNbr = '002320')
	OR (TranType = 'INV' AND RefNbr = '002321')
	OR (TranType = 'INV' AND RefNbr = '002322')
	OR (TranType = 'INV' AND RefNbr = '002323')
	OR (TranType = 'INV' AND RefNbr = '002324')
	OR (TranType = 'INV' AND RefNbr = '002325')
	OR (TranType = 'INV' AND RefNbr = '002326')
	OR (TranType = 'INV' AND RefNbr = '002327')
	OR (TranType = 'INV' AND RefNbr = '002328')
	OR (TranType = 'INV' AND RefNbr = '002329')
	OR (TranType = 'INV' AND RefNbr = '002330')
	OR (TranType = 'INV' AND RefNbr = '002331')
	OR (TranType = 'INV' AND RefNbr = '002332')
	OR (TranType = 'INV' AND RefNbr = '002333')
	OR (TranType = 'INV' AND RefNbr = '002334')
	OR (TranType = 'INV' AND RefNbr = '002335')
	OR (TranType = 'INV' AND RefNbr = '002336')
	OR (TranType = 'INV' AND RefNbr = '002337')
	OR (TranType = 'INV' AND RefNbr = '002338')
	OR (TranType = 'INV' AND RefNbr = '002339')
	OR (TranType = 'INV' AND RefNbr = '002340')
	OR (TranType = 'INV' AND RefNbr = '002341')
	OR (TranType = 'INV' AND RefNbr = '002342')
	OR (TranType = 'INV' AND RefNbr = '002343')
	OR (TranType = 'INV' AND RefNbr = '002344')
	OR (TranType = 'INV' AND RefNbr = '002345')
	OR (TranType = 'INV' AND RefNbr = '002346')
	OR (TranType = 'INV' AND RefNbr = '002347')
	OR (TranType = 'INV' AND RefNbr = '002348')
	OR (TranType = 'INV' AND RefNbr = '002349')
	OR (TranType = 'INV' AND RefNbr = '002350')
	OR (TranType = 'INV' AND RefNbr = '002351')
	OR (TranType = 'INV' AND RefNbr = '002352')
	OR (TranType = 'INV' AND RefNbr = '002353')
	OR (TranType = 'INV' AND RefNbr = '002354')
	OR (TranType = 'INV' AND RefNbr = '002355')
	OR (TranType = 'INV' AND RefNbr = '002356')
	OR (TranType = 'INV' AND RefNbr = '002357')
	OR (TranType = 'INV' AND RefNbr = '002358')
	OR (TranType = 'INV' AND RefNbr = '002359')
	OR (TranType = 'INV' AND RefNbr = '002360')
	OR (TranType = 'INV' AND RefNbr = '002361')
	OR (TranType = 'INV' AND RefNbr = '002362')
	OR (TranType = 'INV' AND RefNbr = '002363')
	OR (TranType = 'INV' AND RefNbr = '002364')
	OR (TranType = 'INV' AND RefNbr = '002365')
	OR (TranType = 'INV' AND RefNbr = '002366')
	OR (TranType = 'INV' AND RefNbr = '002367')
	OR (TranType = 'INV' AND RefNbr = '002368')
	OR (TranType = 'INV' AND RefNbr = '002369')
	OR (TranType = 'INV' AND RefNbr = '002370')
	OR (TranType = 'INV' AND RefNbr = '002371')
	OR (TranType = 'INV' AND RefNbr = '002372')
	OR (TranType = 'INV' AND RefNbr = '002373')
	OR (TranType = 'INV' AND RefNbr = '002374')
	OR (TranType = 'INV' AND RefNbr = '002375')
	OR (TranType = 'INV' AND RefNbr = '002376')
	OR (TranType = 'INV' AND RefNbr = '002377')
	OR (TranType = 'INV' AND RefNbr = '002378')
	OR (TranType = 'INV' AND RefNbr = '002379')
	OR (TranType = 'INV' AND RefNbr = '002380')
	OR (TranType = 'INV' AND RefNbr = '002381')
	OR (TranType = 'INV' AND RefNbr = '002382')
	OR (TranType = 'INV' AND RefNbr = '002383')
	OR (TranType = 'INV' AND RefNbr = '002384')
	OR (TranType = 'INV' AND RefNbr = '002385')
	OR (TranType = 'INV' AND RefNbr = '002386')
	OR (TranType = 'INV' AND RefNbr = '002387')
	OR (TranType = 'INV' AND RefNbr = '002388')
	OR (TranType = 'INV' AND RefNbr = '002389')
	OR (TranType = 'INV' AND RefNbr = '002390')
	OR (TranType = 'INV' AND RefNbr = '002391')
	OR (TranType = 'INV' AND RefNbr = '002392')
	OR (TranType = 'INV' AND RefNbr = '002393')
	OR (TranType = 'INV' AND RefNbr = '002394')
	OR (TranType = 'INV' AND RefNbr = '002395')
	OR (TranType = 'INV' AND RefNbr = '002396')
	OR (TranType = 'INV' AND RefNbr = '002397')
	OR (TranType = 'INV' AND RefNbr = '002398')
	OR (TranType = 'INV' AND RefNbr = '002399')
	OR (TranType = 'INV' AND RefNbr = '002400')
	OR (TranType = 'INV' AND RefNbr = '002401')
	OR (TranType = 'INV' AND RefNbr = '002402')
	OR (TranType = 'INV' AND RefNbr = '002403')
	OR (TranType = 'INV' AND RefNbr = '002404')
	OR (TranType = 'INV' AND RefNbr = '002405')
	OR (TranType = 'INV' AND RefNbr = '002406')
	OR (TranType = 'INV' AND RefNbr = '002407')
	OR (TranType = 'INV' AND RefNbr = '002408')
	OR (TranType = 'INV' AND RefNbr = '002409')
	OR (TranType = 'INV' AND RefNbr = '002410')
	OR (TranType = 'INV' AND RefNbr = '002411')
	OR (TranType = 'INV' AND RefNbr = '002412')
	OR (TranType = 'INV' AND RefNbr = '002413')
	OR (TranType = 'INV' AND RefNbr = '002414')
	OR (TranType = 'INV' AND RefNbr = '002415')
	OR (TranType = 'INV' AND RefNbr = '002416')
	OR (TranType = 'INV' AND RefNbr = '002417')
	OR (TranType = 'INV' AND RefNbr = '002418')
	OR (TranType = 'INV' AND RefNbr = '002419')
	OR (TranType = 'INV' AND RefNbr = '002420')
	OR (TranType = 'INV' AND RefNbr = '002421')
	OR (TranType = 'INV' AND RefNbr = '002422')
	OR (TranType = 'INV' AND RefNbr = '002423')
	OR (TranType = 'INV' AND RefNbr = '002424')
	OR (TranType = 'INV' AND RefNbr = '002425')
	OR (TranType = 'INV' AND RefNbr = '002426')
	OR (TranType = 'INV' AND RefNbr = '002427')
	OR (TranType = 'INV' AND RefNbr = '002428')
	OR (TranType = 'INV' AND RefNbr = '002429')
	OR (TranType = 'INV' AND RefNbr = '002430')
	OR (TranType = 'INV' AND RefNbr = '002431')
	OR (TranType = 'INV' AND RefNbr = '002432')
	OR (TranType = 'INV' AND RefNbr = '002433')
	OR (TranType = 'INV' AND RefNbr = '002434')
	OR (TranType = 'INV' AND RefNbr = '002435')
	OR (TranType = 'INV' AND RefNbr = '002436')
	OR (TranType = 'INV' AND RefNbr = '002437')
	OR (TranType = 'INV' AND RefNbr = '002438')
	OR (TranType = 'INV' AND RefNbr = '002439')
	OR (TranType = 'INV' AND RefNbr = '002440')
	OR (TranType = 'INV' AND RefNbr = '002441')
	OR (TranType = 'INV' AND RefNbr = '002442')
	OR (TranType = 'INV' AND RefNbr = '002443')
	OR (TranType = 'INV' AND RefNbr = '002444')
	OR (TranType = 'INV' AND RefNbr = '002445')
	OR (TranType = 'INV' AND RefNbr = '002446')
	OR (TranType = 'INV' AND RefNbr = '002447')
	OR (TranType = 'INV' AND RefNbr = '002448')
	OR (TranType = 'INV' AND RefNbr = '002449')
	OR (TranType = 'INV' AND RefNbr = '002450')
	OR (TranType = 'INV' AND RefNbr = '002451')
	OR (TranType = 'INV' AND RefNbr = '002452')
	OR (TranType = 'INV' AND RefNbr = '002453')
	OR (TranType = 'INV' AND RefNbr = '002454')
	OR (TranType = 'INV' AND RefNbr = '002458')
	OR (TranType = 'INV' AND RefNbr = '002459')
	OR (TranType = 'INV' AND RefNbr = '002460')
	OR (TranType = 'INV' AND RefNbr = '002461')
	OR (TranType = 'INV' AND RefNbr = '002462')
	OR (TranType = 'INV' AND RefNbr = '002463')
	OR (TranType = 'INV' AND RefNbr = '002464')
	OR (TranType = 'INV' AND RefNbr = '002465')
	OR (TranType = 'INV' AND RefNbr = '002466')
	OR (TranType = 'INV' AND RefNbr = '002467')
	OR (TranType = 'INV' AND RefNbr = '002468')
	OR (TranType = 'INV' AND RefNbr = '002469')
	OR (TranType = 'INV' AND RefNbr = '002470')
	OR (TranType = 'INV' AND RefNbr = '002471')
	OR (TranType = 'INV' AND RefNbr = '002472')
	OR (TranType = 'INV' AND RefNbr = '002473')
	OR (TranType = 'INV' AND RefNbr = '002474')
	OR (TranType = 'INV' AND RefNbr = '002475')
	OR (TranType = 'INV' AND RefNbr = '002476')
	OR (TranType = 'INV' AND RefNbr = '002477')
	OR (TranType = 'INV' AND RefNbr = '002478')
	OR (TranType = 'INV' AND RefNbr = '002479')
	OR (TranType = 'INV' AND RefNbr = '002480')
	OR (TranType = 'INV' AND RefNbr = '002481')
	OR (TranType = 'INV' AND RefNbr = '002482')
	OR (TranType = 'INV' AND RefNbr = '002483')
	OR (TranType = 'INV' AND RefNbr = '002484')
	OR (TranType = 'INV' AND RefNbr = '002485')
	OR (TranType = 'INV' AND RefNbr = '002486')
	OR (TranType = 'INV' AND RefNbr = '002487')
	OR (TranType = 'INV' AND RefNbr = '002488')
	OR (TranType = 'INV' AND RefNbr = '002489')
	OR (TranType = 'INV' AND RefNbr = '002490')
	OR (TranType = 'INV' AND RefNbr = '002491')
	OR (TranType = 'INV' AND RefNbr = '002492')
	OR (TranType = 'INV' AND RefNbr = '002493')
	OR (TranType = 'INV' AND RefNbr = '002494')
	OR (TranType = 'INV' AND RefNbr = '002495')
	OR (TranType = 'INV' AND RefNbr = '002496')
	OR (TranType = 'INV' AND RefNbr = '002497')
	OR (TranType = 'INV' AND RefNbr = '002498')
	OR (TranType = 'INV' AND RefNbr = '002499')
	OR (TranType = 'INV' AND RefNbr = '002500')
	OR (TranType = 'INV' AND RefNbr = '002501')
	OR (TranType = 'INV' AND RefNbr = '002502')
	OR (TranType = 'INV' AND RefNbr = '002503')
	OR (TranType = 'INV' AND RefNbr = '002504')
	OR (TranType = 'INV' AND RefNbr = '002505')
	OR (TranType = 'INV' AND RefNbr = '002506')
	OR (TranType = 'INV' AND RefNbr = '002507')
	OR (TranType = 'INV' AND RefNbr = '002508')
	OR (TranType = 'INV' AND RefNbr = '002509')
	OR (TranType = 'INV' AND RefNbr = '002510')
	OR (TranType = 'INV' AND RefNbr = '002511')
	OR (TranType = 'INV' AND RefNbr = '002512')
	OR (TranType = 'INV' AND RefNbr = '002513')
	OR (TranType = 'INV' AND RefNbr = '002514')
	OR (TranType = 'INV' AND RefNbr = '002515')
	OR (TranType = 'INV' AND RefNbr = '002516')
	OR (TranType = 'INV' AND RefNbr = '002517')
	OR (TranType = 'INV' AND RefNbr = '002518')
	OR (TranType = 'INV' AND RefNbr = '002519')
	OR (TranType = 'INV' AND RefNbr = '002520')
	OR (TranType = 'INV' AND RefNbr = '002521')
	OR (TranType = 'INV' AND RefNbr = '002522')
	OR (TranType = 'INV' AND RefNbr = '002523')
	OR (TranType = 'INV' AND RefNbr = '002524')
	OR (TranType = 'INV' AND RefNbr = '002525')
	OR (TranType = 'INV' AND RefNbr = '002526')
	OR (TranType = 'INV' AND RefNbr = '002527')
	OR (TranType = 'INV' AND RefNbr = '002528')
	OR (TranType = 'INV' AND RefNbr = '002529')
	OR (TranType = 'INV' AND RefNbr = '002530')
	OR (TranType = 'INV' AND RefNbr = '002531')
	OR (TranType = 'INV' AND RefNbr = '002532')
	OR (TranType = 'INV' AND RefNbr = '002533')
	OR (TranType = 'INV' AND RefNbr = '002534')
	OR (TranType = 'INV' AND RefNbr = '002535')
	OR (TranType = 'INV' AND RefNbr = '002536')
	OR (TranType = 'INV' AND RefNbr = '002537')
	OR (TranType = 'INV' AND RefNbr = '002538')
	OR (TranType = 'INV' AND RefNbr = '002539')
	OR (TranType = 'INV' AND RefNbr = '002540')
	OR (TranType = 'INV' AND RefNbr = '002541')
	OR (TranType = 'INV' AND RefNbr = '002542')
	OR (TranType = 'INV' AND RefNbr = '002543')
	OR (TranType = 'INV' AND RefNbr = '002544')
	OR (TranType = 'INV' AND RefNbr = '002545')
	OR (TranType = 'INV' AND RefNbr = '002546')
	OR (TranType = 'INV' AND RefNbr = '002547')
	OR (TranType = 'INV' AND RefNbr = '002548')
	OR (TranType = 'INV' AND RefNbr = '002549')
	OR (TranType = 'INV' AND RefNbr = '002550')
	OR (TranType = 'INV' AND RefNbr = '002551')
	OR (TranType = 'INV' AND RefNbr = '002552')
	OR (TranType = 'INV' AND RefNbr = '002553')
	OR (TranType = 'INV' AND RefNbr = '002554')
	OR (TranType = 'INV' AND RefNbr = '002555')
	OR (TranType = 'INV' AND RefNbr = '002556')
	OR (TranType = 'INV' AND RefNbr = '002557')
	OR (TranType = 'INV' AND RefNbr = '002558')
	OR (TranType = 'INV' AND RefNbr = '002559')
	OR (TranType = 'INV' AND RefNbr = '002560')
	OR (TranType = 'INV' AND RefNbr = '002561')
	OR (TranType = 'INV' AND RefNbr = '002562')
	OR (TranType = 'INV' AND RefNbr = '002563')
	OR (TranType = 'INV' AND RefNbr = '002564')
	OR (TranType = 'INV' AND RefNbr = '002565')
	OR (TranType = 'INV' AND RefNbr = '002566')
	OR (TranType = 'INV' AND RefNbr = '002567')
	OR (TranType = 'INV' AND RefNbr = '002568')
	OR (TranType = 'INV' AND RefNbr = '002569')
	OR (TranType = 'INV' AND RefNbr = '002570')
	OR (TranType = 'INV' AND RefNbr = '002571')
	OR (TranType = 'INV' AND RefNbr = '002572')
	OR (TranType = 'INV' AND RefNbr = '002573')
	OR (TranType = 'INV' AND RefNbr = '002574')
	OR (TranType = 'INV' AND RefNbr = '002575')
	OR (TranType = 'INV' AND RefNbr = '002576')
	OR (TranType = 'INV' AND RefNbr = '002577')
	OR (TranType = 'INV' AND RefNbr = '002578')
	OR (TranType = 'INV' AND RefNbr = '002579')
	OR (TranType = 'INV' AND RefNbr = '002580')
	OR (TranType = 'INV' AND RefNbr = '002581')
	OR (TranType = 'INV' AND RefNbr = '002582')
	OR (TranType = 'INV' AND RefNbr = '002583')
	OR (TranType = 'INV' AND RefNbr = '002584')
	OR (TranType = 'INV' AND RefNbr = '002585')
	OR (TranType = 'INV' AND RefNbr = '002586')
	OR (TranType = 'INV' AND RefNbr = '002587')
	OR (TranType = 'INV' AND RefNbr = '002588')
	OR (TranType = 'INV' AND RefNbr = '002589')
	OR (TranType = 'INV' AND RefNbr = '002590')
	OR (TranType = 'INV' AND RefNbr = '002591')
	OR (TranType = 'INV' AND RefNbr = '002592')
	OR (TranType = 'INV' AND RefNbr = '002593')
	OR (TranType = 'INV' AND RefNbr = '002594')
	OR (TranType = 'INV' AND RefNbr = '002595')
	OR (TranType = 'INV' AND RefNbr = '002596')
	OR (TranType = 'INV' AND RefNbr = '002597')
	OR (TranType = 'INV' AND RefNbr = '002598')
	OR (TranType = 'INV' AND RefNbr = '002599')
	OR (TranType = 'INV' AND RefNbr = '002600')
	OR (TranType = 'INV' AND RefNbr = '002601')
	OR (TranType = 'INV' AND RefNbr = '002602')
	OR (TranType = 'INV' AND RefNbr = '002603')
	OR (TranType = 'INV' AND RefNbr = '002604')
	OR (TranType = 'INV' AND RefNbr = '002605')
	OR (TranType = 'INV' AND RefNbr = '002606')
	OR (TranType = 'INV' AND RefNbr = '002607')
	OR (TranType = 'INV' AND RefNbr = '002608')
	OR (TranType = 'INV' AND RefNbr = '002713')
	OR (TranType = 'INV' AND RefNbr = '002714')
	OR (TranType = 'INV' AND RefNbr = '002715')
	OR (TranType = 'INV' AND RefNbr = '002716')
	OR (TranType = 'INV' AND RefNbr = '002717')
	OR (TranType = 'INV' AND RefNbr = '002718')
	OR (TranType = 'INV' AND RefNbr = '002719')
	OR (TranType = 'INV' AND RefNbr = '002720')
	OR (TranType = 'INV' AND RefNbr = '002721')
	OR (TranType = 'INV' AND RefNbr = '002722')
	OR (TranType = 'INV' AND RefNbr = '002723')
	OR (TranType = 'INV' AND RefNbr = '002724')
	OR (TranType = 'INV' AND RefNbr = '002725')
	OR (TranType = 'INV' AND RefNbr = '002726')
	OR (TranType = 'INV' AND RefNbr = '002727')
	OR (TranType = 'INV' AND RefNbr = '002728')
	OR (TranType = 'INV' AND RefNbr = '002729')
	OR (TranType = 'INV' AND RefNbr = '002730')
	OR (TranType = 'INV' AND RefNbr = '002731')
	OR (TranType = 'INV' AND RefNbr = '002732')
	OR (TranType = 'INV' AND RefNbr = '002733')
	OR (TranType = 'INV' AND RefNbr = '002734')
	OR (TranType = 'INV' AND RefNbr = '002735')
	OR (TranType = 'INV' AND RefNbr = '002736')
	OR (TranType = 'INV' AND RefNbr = '002737')
	OR (TranType = 'INV' AND RefNbr = '002738')
	OR (TranType = 'INV' AND RefNbr = '002739')
	OR (TranType = 'INV' AND RefNbr = '002740')
	OR (TranType = 'INV' AND RefNbr = '002741')
	OR (TranType = 'INV' AND RefNbr = '002742')
	OR (TranType = 'INV' AND RefNbr = '002743')
	OR (TranType = 'INV' AND RefNbr = '002744')
	OR (TranType = 'INV' AND RefNbr = '002745')
	OR (TranType = 'INV' AND RefNbr = '002746')
	OR (TranType = 'INV' AND RefNbr = '002747')
	OR (TranType = 'INV' AND RefNbr = '002748')
	OR (TranType = 'INV' AND RefNbr = '002749')
	OR (TranType = 'INV' AND RefNbr = '002750')
	OR (TranType = 'INV' AND RefNbr = '002751')
	OR (TranType = 'INV' AND RefNbr = '002752')
	OR (TranType = 'INV' AND RefNbr = '002753')
	OR (TranType = 'INV' AND RefNbr = '002754')
	OR (TranType = 'INV' AND RefNbr = '002755')
	OR (TranType = 'INV' AND RefNbr = '002756')
	OR (TranType = 'INV' AND RefNbr = '002757')
	OR (TranType = 'INV' AND RefNbr = '002758')
	OR (TranType = 'INV' AND RefNbr = '002759')
	OR (TranType = 'INV' AND RefNbr = '002760')
	OR (TranType = 'INV' AND RefNbr = '002761')
	OR (TranType = 'INV' AND RefNbr = '002762')
	OR (TranType = 'INV' AND RefNbr = '002763')
	OR (TranType = 'INV' AND RefNbr = '002764')
	OR (TranType = 'INV' AND RefNbr = '002765')
	OR (TranType = 'INV' AND RefNbr = '002766')
	OR (TranType = 'INV' AND RefNbr = '002767')
	OR (TranType = 'INV' AND RefNbr = '002768')
	OR (TranType = 'INV' AND RefNbr = '002769')
	OR (TranType = 'INV' AND RefNbr = '002770')
	OR (TranType = 'INV' AND RefNbr = '002771')
	OR (TranType = 'INV' AND RefNbr = '002772')
	OR (TranType = 'INV' AND RefNbr = '002773')
	OR (TranType = 'INV' AND RefNbr = '002774')
	OR (TranType = 'INV' AND RefNbr = '002775')
	OR (TranType = 'INV' AND RefNbr = '002776')
	OR (TranType = 'INV' AND RefNbr = '002777')
	OR (TranType = 'INV' AND RefNbr = '002778')
	OR (TranType = 'INV' AND RefNbr = '002779')
	OR (TranType = 'INV' AND RefNbr = '002780')
	OR (TranType = 'INV' AND RefNbr = '002781')
	OR (TranType = 'INV' AND RefNbr = '002782')
	OR (TranType = 'INV' AND RefNbr = '002783')
	OR (TranType = 'INV' AND RefNbr = '002784')
	OR (TranType = 'INV' AND RefNbr = '002785')
	OR (TranType = 'INV' AND RefNbr = '002786')
	OR (TranType = 'INV' AND RefNbr = '002787')
	OR (TranType = 'INV' AND RefNbr = '002788')
	OR (TranType = 'INV' AND RefNbr = '002789')
	OR (TranType = 'INV' AND RefNbr = '002790')
	OR (TranType = 'INV' AND RefNbr = '002791')
	OR (TranType = 'INV' AND RefNbr = '002792')
	OR (TranType = 'INV' AND RefNbr = '002793')
	OR (TranType = 'INV' AND RefNbr = '002794')
	OR (TranType = 'INV' AND RefNbr = '002795')
	OR (TranType = 'INV' AND RefNbr = '002796')
	OR (TranType = 'INV' AND RefNbr = '002797')
	OR (TranType = 'INV' AND RefNbr = '002798')
	OR (TranType = 'INV' AND RefNbr = '002799')
	OR (TranType = 'INV' AND RefNbr = '002800')
	OR (TranType = 'INV' AND RefNbr = '002801')
	OR (TranType = 'INV' AND RefNbr = '002802')
	OR (TranType = 'INV' AND RefNbr = '002803')
	OR (TranType = 'INV' AND RefNbr = '002804')
	OR (TranType = 'INV' AND RefNbr = '002805')
	OR (TranType = 'INV' AND RefNbr = '002806')
	OR (TranType = 'INV' AND RefNbr = '002807')
	OR (TranType = 'INV' AND RefNbr = '002808')
	OR (TranType = 'INV' AND RefNbr = '002809')
	OR (TranType = 'INV' AND RefNbr = '002810')
	OR (TranType = 'INV' AND RefNbr = '002811')
	OR (TranType = 'INV' AND RefNbr = '002812')
	OR (TranType = 'INV' AND RefNbr = '002813')
	OR (TranType = 'INV' AND RefNbr = '002814')
	OR (TranType = 'INV' AND RefNbr = '002815')
	OR (TranType = 'INV' AND RefNbr = '002816')
	OR (TranType = 'INV' AND RefNbr = '002817')
	OR (TranType = 'INV' AND RefNbr = '002818')
	OR (TranType = 'INV' AND RefNbr = '002819')
	OR (TranType = 'INV' AND RefNbr = '002820')
	OR (TranType = 'INV' AND RefNbr = '002821')
	OR (TranType = 'INV' AND RefNbr = '002822')
	OR (TranType = 'INV' AND RefNbr = '002823')
	OR (TranType = 'INV' AND RefNbr = '002824')
	OR (TranType = 'INV' AND RefNbr = '002825')
	OR (TranType = 'INV' AND RefNbr = '002826')
	OR (TranType = 'INV' AND RefNbr = '002827')
	OR (TranType = 'INV' AND RefNbr = '002828')
	OR (TranType = 'INV' AND RefNbr = '002829')
	OR (TranType = 'INV' AND RefNbr = '002830')
	OR (TranType = 'INV' AND RefNbr = '002831')
	OR (TranType = 'INV' AND RefNbr = '002832')
	OR (TranType = 'INV' AND RefNbr = '002833')
	OR (TranType = 'INV' AND RefNbr = '002834')
	OR (TranType = 'INV' AND RefNbr = '002835')
	OR (TranType = 'INV' AND RefNbr = '002836')
	OR (TranType = 'INV' AND RefNbr = '002837')
	OR (TranType = 'INV' AND RefNbr = '002838')
	OR (TranType = 'INV' AND RefNbr = '002839')
	OR (TranType = 'INV' AND RefNbr = '002840')
	OR (TranType = 'INV' AND RefNbr = '002841')
	OR (TranType = 'INV' AND RefNbr = '002842')
	OR (TranType = 'INV' AND RefNbr = '002843')
	OR (TranType = 'INV' AND RefNbr = '002844')
	OR (TranType = 'INV' AND RefNbr = '002845')
	OR (TranType = 'INV' AND RefNbr = '002846')
	OR (TranType = 'INV' AND RefNbr = '002847')
	OR (TranType = 'INV' AND RefNbr = '002848')
	OR (TranType = 'INV' AND RefNbr = '002849')
	OR (TranType = 'INV' AND RefNbr = '002850')
	OR (TranType = 'INV' AND RefNbr = '002851')
	OR (TranType = 'INV' AND RefNbr = '002852')
	OR (TranType = 'INV' AND RefNbr = '002853')
	OR (TranType = 'INV' AND RefNbr = '002854')
	OR (TranType = 'INV' AND RefNbr = '002855')
	OR (TranType = 'INV' AND RefNbr = '002856')
	OR (TranType = 'INV' AND RefNbr = '002857')
	OR (TranType = 'INV' AND RefNbr = '002858')
	OR (TranType = 'INV' AND RefNbr = '002859')
	OR (TranType = 'INV' AND RefNbr = '002860')
	OR (TranType = 'INV' AND RefNbr = '002861')
	OR (TranType = 'INV' AND RefNbr = '002862')
	OR (TranType = 'INV' AND RefNbr = '002863')
	OR (TranType = 'INV' AND RefNbr = '002864')
	OR (TranType = 'INV' AND RefNbr = '002865')
	OR (TranType = 'INV' AND RefNbr = '002866')
	OR (TranType = 'INV' AND RefNbr = '002867')
	OR (TranType = 'INV' AND RefNbr = '002868')
	OR (TranType = 'INV' AND RefNbr = '002869')
	OR (TranType = 'INV' AND RefNbr = '002870')
	OR (TranType = 'INV' AND RefNbr = '002871')
	OR (TranType = 'INV' AND RefNbr = '002872')
	OR (TranType = 'INV' AND RefNbr = '002873')
	OR (TranType = 'INV' AND RefNbr = '002874')
	OR (TranType = 'INV' AND RefNbr = '002875')
	OR (TranType = 'INV' AND RefNbr = '002876')
	OR (TranType = 'INV' AND RefNbr = '002877')
	OR (TranType = 'INV' AND RefNbr = '002878')
	OR (TranType = 'INV' AND RefNbr = '002879')
	OR (TranType = 'INV' AND RefNbr = '002880')
	OR (TranType = 'INV' AND RefNbr = '002881')
	OR (TranType = 'INV' AND RefNbr = '002882')
	OR (TranType = 'INV' AND RefNbr = '002883')
	OR (TranType = 'INV' AND RefNbr = '002884')
	OR (TranType = 'INV' AND RefNbr = '002885')
	OR (TranType = 'INV' AND RefNbr = '002886')
	OR (TranType = 'INV' AND RefNbr = '002887')
	OR (TranType = 'INV' AND RefNbr = '002888')
	OR (TranType = 'INV' AND RefNbr = '002889')
	OR (TranType = 'INV' AND RefNbr = '002890')
	OR (TranType = 'INV' AND RefNbr = '002891')
	OR (TranType = 'INV' AND RefNbr = '002892')
	OR (TranType = 'INV' AND RefNbr = '002893')
	OR (TranType = 'INV' AND RefNbr = '002894')
	OR (TranType = 'INV' AND RefNbr = '002895')
	OR (TranType = 'INV' AND RefNbr = '002896')
	OR (TranType = 'INV' AND RefNbr = '002897')
	OR (TranType = 'INV' AND RefNbr = '002898')
	OR (TranType = 'INV' AND RefNbr = '002899')
	OR (TranType = 'INV' AND RefNbr = '002900')
	OR (TranType = 'INV' AND RefNbr = '002901')
	OR (TranType = 'INV' AND RefNbr = '002902')
	OR (TranType = 'INV' AND RefNbr = '002903')
	OR (TranType = 'INV' AND RefNbr = '002904')
	OR (TranType = 'INV' AND RefNbr = '002905')
	OR (TranType = 'INV' AND RefNbr = '002906')
	OR (TranType = 'INV' AND RefNbr = '002907')
	OR (TranType = 'INV' AND RefNbr = '002908')
	OR (TranType = 'INV' AND RefNbr = '002909')
	OR (TranType = 'INV' AND RefNbr = '002910')
	OR (TranType = 'INV' AND RefNbr = '002911')
	OR (TranType = 'INV' AND RefNbr = '002912')
	OR (TranType = 'INV' AND RefNbr = '002913')
	OR (TranType = 'INV' AND RefNbr = '002914')
	OR (TranType = 'INV' AND RefNbr = '002915')
	OR (TranType = 'INV' AND RefNbr = '002916')
	OR (TranType = 'INV' AND RefNbr = '002917')
	OR (TranType = 'INV' AND RefNbr = '002918')
	OR (TranType = 'INV' AND RefNbr = '002919')
	OR (TranType = 'INV' AND RefNbr = '002920')
	OR (TranType = 'INV' AND RefNbr = '002921')
	OR (TranType = 'INV' AND RefNbr = '002922')
	OR (TranType = 'INV' AND RefNbr = '002923')
	OR (TranType = 'INV' AND RefNbr = '002924')
	OR (TranType = 'INV' AND RefNbr = '002925')
	OR (TranType = 'INV' AND RefNbr = '002926')
	OR (TranType = 'INV' AND RefNbr = '002927')
	OR (TranType = 'INV' AND RefNbr = '002928')
	OR (TranType = 'INV' AND RefNbr = '002929')
	OR (TranType = 'INV' AND RefNbr = '002930')
	OR (TranType = 'INV' AND RefNbr = '002931')
	OR (TranType = 'INV' AND RefNbr = '002932')
	OR (TranType = 'INV' AND RefNbr = '002933')
	OR (TranType = 'INV' AND RefNbr = '002934')
	OR (TranType = 'INV' AND RefNbr = '002935')
	OR (TranType = 'INV' AND RefNbr = '002936')
	OR (TranType = 'INV' AND RefNbr = '002937')
	OR (TranType = 'INV' AND RefNbr = '002938')
	OR (TranType = 'INV' AND RefNbr = '002939')
	OR (TranType = 'INV' AND RefNbr = '002940')
	OR (TranType = 'INV' AND RefNbr = '002941')
	OR (TranType = 'INV' AND RefNbr = '002942')
	OR (TranType = 'INV' AND RefNbr = '002943')
	OR (TranType = 'INV' AND RefNbr = '002944')
	OR (TranType = 'INV' AND RefNbr = '002945')
	OR (TranType = 'INV' AND RefNbr = '002946')
	OR (TranType = 'INV' AND RefNbr = '002947')
	OR (TranType = 'INV' AND RefNbr = '002948')
	OR (TranType = 'INV' AND RefNbr = '002949')
	OR (TranType = 'INV' AND RefNbr = '002950')
	OR (TranType = 'INV' AND RefNbr = '002951')
	OR (TranType = 'INV' AND RefNbr = '002952')
	OR (TranType = 'INV' AND RefNbr = '002953')
	OR (TranType = 'INV' AND RefNbr = '002954')
	OR (TranType = 'INV' AND RefNbr = '002955')
	OR (TranType = 'INV' AND RefNbr = '002956')
	OR (TranType = 'INV' AND RefNbr = '002957')
	OR (TranType = 'INV' AND RefNbr = '002958')
	OR (TranType = 'INV' AND RefNbr = '002959')
	OR (TranType = 'INV' AND RefNbr = '002960')
	OR (TranType = 'INV' AND RefNbr = '002961')
	OR (TranType = 'INV' AND RefNbr = '002962')
	OR (TranType = 'INV' AND RefNbr = '002963')
	OR (TranType = 'INV' AND RefNbr = '002964')
	OR (TranType = 'INV' AND RefNbr = '002965')
	OR (TranType = 'INV' AND RefNbr = '002966')
	OR (TranType = 'INV' AND RefNbr = '002967')
	OR (TranType = 'INV' AND RefNbr = '002968')
	OR (TranType = 'INV' AND RefNbr = '002969')
	OR (TranType = 'INV' AND RefNbr = '002970')
	OR (TranType = 'INV' AND RefNbr = '002971')
	OR (TranType = 'INV' AND RefNbr = '002972')
	OR (TranType = 'INV' AND RefNbr = '002973')
	OR (TranType = 'INV' AND RefNbr = '002974')
	OR (TranType = 'INV' AND RefNbr = '002975')
	OR (TranType = 'INV' AND RefNbr = '002976')
	OR (TranType = 'INV' AND RefNbr = '002977')
	OR (TranType = 'INV' AND RefNbr = '002978')
	OR (TranType = 'INV' AND RefNbr = '002979')
	OR (TranType = 'INV' AND RefNbr = '002980')
	OR (TranType = 'INV' AND RefNbr = '002981')
	OR (TranType = 'INV' AND RefNbr = '002982')
	OR (TranType = 'INV' AND RefNbr = '002983')
	OR (TranType = 'INV' AND RefNbr = '002984')
	OR (TranType = 'INV' AND RefNbr = '002985')
	OR (TranType = 'INV' AND RefNbr = '002986')
	OR (TranType = 'INV' AND RefNbr = '002987')
	OR (TranType = 'INV' AND RefNbr = '002988')
	OR (TranType = 'INV' AND RefNbr = '002989')
	OR (TranType = 'INV' AND RefNbr = '002990')
	OR (TranType = 'INV' AND RefNbr = '002991')
	OR (TranType = 'INV' AND RefNbr = '002992')
	OR (TranType = 'INV' AND RefNbr = '002993')
	OR (TranType = 'INV' AND RefNbr = '002994')
	OR (TranType = 'INV' AND RefNbr = '002995')
	OR (TranType = 'INV' AND RefNbr = '002996')
	OR (TranType = 'INV' AND RefNbr = '002997')
	OR (TranType = 'INV' AND RefNbr = '002998')
	OR (TranType = 'INV' AND RefNbr = '002999')
	OR (TranType = 'INV' AND RefNbr = '003000')
	OR (TranType = 'INV' AND RefNbr = '003001')
	OR (TranType = 'INV' AND RefNbr = '003002')
	OR (TranType = 'INV' AND RefNbr = '003003')
	OR (TranType = 'INV' AND RefNbr = '003004')
	OR (TranType = 'INV' AND RefNbr = '003005')
	OR (TranType = 'INV' AND RefNbr = '003006')
	OR (TranType = 'INV' AND RefNbr = '003007')
	OR (TranType = 'INV' AND RefNbr = '003008')
	OR (TranType = 'INV' AND RefNbr = '003009')
	OR (TranType = 'INV' AND RefNbr = '003010')
	OR (TranType = 'INV' AND RefNbr = '003011')
	OR (TranType = 'INV' AND RefNbr = '003012')
	OR (TranType = 'INV' AND RefNbr = '003013')
	OR (TranType = 'INV' AND RefNbr = '003014')
	OR (TranType = 'INV' AND RefNbr = '003015')
	OR (TranType = 'INV' AND RefNbr = '003016')
	OR (TranType = 'INV' AND RefNbr = '003017')
	OR (TranType = 'INV' AND RefNbr = '003018')
	OR (TranType = 'INV' AND RefNbr = '003019')
	OR (TranType = 'INV' AND RefNbr = '003020')
	OR (TranType = 'INV' AND RefNbr = '003021')
	OR (TranType = 'INV' AND RefNbr = '003022')
	OR (TranType = 'INV' AND RefNbr = '003023')
	OR (TranType = 'INV' AND RefNbr = '003024')
	OR (TranType = 'INV' AND RefNbr = '003025')
	OR (TranType = 'INV' AND RefNbr = '003026')
	OR (TranType = 'INV' AND RefNbr = '003027')
	OR (TranType = 'INV' AND RefNbr = '003028')
	OR (TranType = 'INV' AND RefNbr = '003029')
	OR (TranType = 'INV' AND RefNbr = '003030')
	OR (TranType = 'INV' AND RefNbr = '003031')
	OR (TranType = 'INV' AND RefNbr = '003032')
	OR (TranType = 'INV' AND RefNbr = '003033')
	OR (TranType = 'INV' AND RefNbr = '003034')
	OR (TranType = 'INV' AND RefNbr = '003035')
	OR (TranType = 'INV' AND RefNbr = '003036')
	OR (TranType = 'INV' AND RefNbr = '003037')
	OR (TranType = 'INV' AND RefNbr = '003038')
	OR (TranType = 'INV' AND RefNbr = '003039')
	OR (TranType = 'INV' AND RefNbr = '003040')
	OR (TranType = 'INV' AND RefNbr = '003041')
	OR (TranType = 'INV' AND RefNbr = '003042')
	OR (TranType = 'INV' AND RefNbr = '003043')
	OR (TranType = 'INV' AND RefNbr = '003044')
	OR (TranType = 'INV' AND RefNbr = '003045')
	OR (TranType = 'INV' AND RefNbr = '003046')
	OR (TranType = 'INV' AND RefNbr = '003047')
	OR (TranType = 'INV' AND RefNbr = '003048')
	OR (TranType = 'INV' AND RefNbr = '003049')
	OR (TranType = 'INV' AND RefNbr = '003050')
	OR (TranType = 'INV' AND RefNbr = '003051')
	OR (TranType = 'INV' AND RefNbr = '003052')
	OR (TranType = 'INV' AND RefNbr = '003053')
	OR (TranType = 'INV' AND RefNbr = '003054')
	OR (TranType = 'INV' AND RefNbr = '003055')
	OR (TranType = 'INV' AND RefNbr = '003056')
	OR (TranType = 'INV' AND RefNbr = '003057')
	OR (TranType = 'INV' AND RefNbr = '003058')
	OR (TranType = 'INV' AND RefNbr = '003059')
	OR (TranType = 'INV' AND RefNbr = '003060')
	OR (TranType = 'INV' AND RefNbr = '003061')
	OR (TranType = 'INV' AND RefNbr = '003062')
	OR (TranType = 'INV' AND RefNbr = '003063')
	OR (TranType = 'INV' AND RefNbr = '003064')
	OR (TranType = 'INV' AND RefNbr = '003065')
	OR (TranType = 'INV' AND RefNbr = '003066')
	OR (TranType = 'INV' AND RefNbr = '003067')
	OR (TranType = 'INV' AND RefNbr = '003068')
	OR (TranType = 'INV' AND RefNbr = '003069')
	OR (TranType = 'INV' AND RefNbr = '003070')
	OR (TranType = 'INV' AND RefNbr = '003071')
	OR (TranType = 'INV' AND RefNbr = '003072')
	OR (TranType = 'INV' AND RefNbr = '003073')
	OR (TranType = 'INV' AND RefNbr = '003074')
	OR (TranType = 'INV' AND RefNbr = '003075')
	OR (TranType = 'INV' AND RefNbr = '003076')
	OR (TranType = 'INV' AND RefNbr = '003077')
	OR (TranType = 'INV' AND RefNbr = '003078')
	OR (TranType = 'INV' AND RefNbr = '003079')
	OR (TranType = 'INV' AND RefNbr = '003080')
	OR (TranType = 'INV' AND RefNbr = '003081')
	OR (TranType = 'INV' AND RefNbr = '003082')
	OR (TranType = 'INV' AND RefNbr = '003083')
	OR (TranType = 'INV' AND RefNbr = '003084')
	OR (TranType = 'INV' AND RefNbr = '003085')
	OR (TranType = 'INV' AND RefNbr = '003086')
	OR (TranType = 'INV' AND RefNbr = '003087')
	OR (TranType = 'INV' AND RefNbr = '003088')
	OR (TranType = 'INV' AND RefNbr = '003089')
	OR (TranType = 'INV' AND RefNbr = '003090')
	OR (TranType = 'INV' AND RefNbr = '003091')
	OR (TranType = 'INV' AND RefNbr = '003092')
	OR (TranType = 'INV' AND RefNbr = '003093')
	OR (TranType = 'INV' AND RefNbr = '003094')
	OR (TranType = 'INV' AND RefNbr = '003095')
	OR (TranType = 'INV' AND RefNbr = '003096')
	OR (TranType = 'INV' AND RefNbr = '003097')
	OR (TranType = 'INV' AND RefNbr = '003098')
	OR (TranType = 'INV' AND RefNbr = '003099')
	OR (TranType = 'INV' AND RefNbr = '003100')
	OR (TranType = 'INV' AND RefNbr = '003101')
	OR (TranType = 'INV' AND RefNbr = '003102')
	OR (TranType = 'INV' AND RefNbr = '003103')
	OR (TranType = 'INV' AND RefNbr = '003104')
	OR (TranType = 'INV' AND RefNbr = '003105')
	OR (TranType = 'INV' AND RefNbr = '003106')
	OR (TranType = 'INV' AND RefNbr = '003107')
	OR (TranType = 'INV' AND RefNbr = '003108')
	OR (TranType = 'INV' AND RefNbr = '003109')
	OR (TranType = 'INV' AND RefNbr = '003110')
	OR (TranType = 'INV' AND RefNbr = '003111')
	OR (TranType = 'INV' AND RefNbr = '003112')
	OR (TranType = 'INV' AND RefNbr = '003113')
	OR (TranType = 'INV' AND RefNbr = '003114')
	OR (TranType = 'INV' AND RefNbr = '003115')
	OR (TranType = 'INV' AND RefNbr = '003116')
	OR (TranType = 'INV' AND RefNbr = '003117')
	OR (TranType = 'INV' AND RefNbr = '003118')
	OR (TranType = 'INV' AND RefNbr = '003119')
	OR (TranType = 'INV' AND RefNbr = '003120')
	OR (TranType = 'INV' AND RefNbr = '003121')
	OR (TranType = 'INV' AND RefNbr = '003122')
	OR (TranType = 'INV' AND RefNbr = '003123')
	OR (TranType = 'INV' AND RefNbr = '003124')
	OR (TranType = 'INV' AND RefNbr = '003125')
	OR (TranType = 'INV' AND RefNbr = '003126')
	OR (TranType = 'INV' AND RefNbr = '003127')
	OR (TranType = 'INV' AND RefNbr = '003128')
	OR (TranType = 'INV' AND RefNbr = '003129')
	OR (TranType = 'INV' AND RefNbr = '003130')
	OR (TranType = 'INV' AND RefNbr = '003131')
	OR (TranType = 'INV' AND RefNbr = '003132')
	OR (TranType = 'INV' AND RefNbr = '003133')
	OR (TranType = 'INV' AND RefNbr = '003134')
	OR (TranType = 'INV' AND RefNbr = '003135')
	OR (TranType = 'INV' AND RefNbr = '003136')
	OR (TranType = 'INV' AND RefNbr = '003137')
	OR (TranType = 'INV' AND RefNbr = '003138')
	OR (TranType = 'INV' AND RefNbr = '003139')
	OR (TranType = 'INV' AND RefNbr = '003140')
	OR (TranType = 'INV' AND RefNbr = '003141')
	OR (TranType = 'INV' AND RefNbr = '003142')
	OR (TranType = 'INV' AND RefNbr = '003143')
	OR (TranType = 'INV' AND RefNbr = '003144')
	OR (TranType = 'INV' AND RefNbr = '003145')
	OR (TranType = 'INV' AND RefNbr = '003146')
	OR (TranType = 'INV' AND RefNbr = '003147')
	OR (TranType = 'INV' AND RefNbr = '003148')
	OR (TranType = 'INV' AND RefNbr = '003149')
	OR (TranType = 'INV' AND RefNbr = '003150')
	OR (TranType = 'INV' AND RefNbr = '003151')
	OR (TranType = 'INV' AND RefNbr = '003152')
	OR (TranType = 'INV' AND RefNbr = '003153')
	OR (TranType = 'INV' AND RefNbr = '003154')
	OR (TranType = 'INV' AND RefNbr = '003155')
	OR (TranType = 'INV' AND RefNbr = '003156')
	OR (TranType = 'INV' AND RefNbr = '003157')
	OR (TranType = 'INV' AND RefNbr = '003158')
	OR (TranType = 'INV' AND RefNbr = '003159')
	OR (TranType = 'INV' AND RefNbr = '003160')
	OR (TranType = 'INV' AND RefNbr = '003161')
	OR (TranType = 'INV' AND RefNbr = '003162')
	OR (TranType = 'INV' AND RefNbr = '003163')
	OR (TranType = 'INV' AND RefNbr = '003164')
	OR (TranType = 'INV' AND RefNbr = '003165')
	OR (TranType = 'INV' AND RefNbr = '003166')
	OR (TranType = 'INV' AND RefNbr = '003167')
	OR (TranType = 'INV' AND RefNbr = '003168')
	OR (TranType = 'INV' AND RefNbr = '003169')
	OR (TranType = 'INV' AND RefNbr = '003170')
	OR (TranType = 'INV' AND RefNbr = '003171')
	OR (TranType = 'INV' AND RefNbr = '003172')
	OR (TranType = 'INV' AND RefNbr = '003173')
	OR (TranType = 'INV' AND RefNbr = '003174')
	OR (TranType = 'INV' AND RefNbr = '003175')
	OR (TranType = 'INV' AND RefNbr = '003176')
	OR (TranType = 'INV' AND RefNbr = '003177')
	OR (TranType = 'INV' AND RefNbr = '003178')
	OR (TranType = 'INV' AND RefNbr = '003179')
	OR (TranType = 'INV' AND RefNbr = '003180')
	OR (TranType = 'INV' AND RefNbr = '003181')
	OR (TranType = 'INV' AND RefNbr = '003182')
	OR (TranType = 'INV' AND RefNbr = '003183')
	OR (TranType = 'INV' AND RefNbr = '003184')
	OR (TranType = 'INV' AND RefNbr = '003185')
	OR (TranType = 'INV' AND RefNbr = '003186')
	OR (TranType = 'INV' AND RefNbr = '003187')
	OR (TranType = 'INV' AND RefNbr = '003188')
	OR (TranType = 'INV' AND RefNbr = '003189')
	OR (TranType = 'INV' AND RefNbr = '003190')
	OR (TranType = 'INV' AND RefNbr = '003191')
	OR (TranType = 'INV' AND RefNbr = '003192')
	OR (TranType = 'INV' AND RefNbr = '003193')
	OR (TranType = 'INV' AND RefNbr = '003194')
	OR (TranType = 'INV' AND RefNbr = '003195')
	OR (TranType = 'INV' AND RefNbr = '003196')
	OR (TranType = 'INV' AND RefNbr = '003197')
	OR (TranType = 'INV' AND RefNbr = '003198')
	OR (TranType = 'INV' AND RefNbr = '003199')
	OR (TranType = 'INV' AND RefNbr = '003200')
	OR (TranType = 'INV' AND RefNbr = '003201')
	OR (TranType = 'INV' AND RefNbr = '003202')
	OR (TranType = 'INV' AND RefNbr = '003203')
	OR (TranType = 'INV' AND RefNbr = '003204')
	OR (TranType = 'INV' AND RefNbr = '003205')
	OR (TranType = 'INV' AND RefNbr = '003206')
	OR (TranType = 'INV' AND RefNbr = '003207')
	OR (TranType = 'INV' AND RefNbr = '003208')
	OR (TranType = 'INV' AND RefNbr = '003209')
	OR (TranType = 'INV' AND RefNbr = '003210')
	OR (TranType = 'INV' AND RefNbr = '003211')
	OR (TranType = 'INV' AND RefNbr = '003212')
	OR (TranType = 'INV' AND RefNbr = '003213')
	OR (TranType = 'INV' AND RefNbr = '003214')
	OR (TranType = 'INV' AND RefNbr = '003215')
	OR (TranType = 'INV' AND RefNbr = '003216')
	OR (TranType = 'INV' AND RefNbr = '003217')
	OR (TranType = 'INV' AND RefNbr = '003218')
	OR (TranType = 'INV' AND RefNbr = '003219')
	OR (TranType = 'INV' AND RefNbr = '003220')
	OR (TranType = 'INV' AND RefNbr = '003221')
	OR (TranType = 'INV' AND RefNbr = '003222')
	OR (TranType = 'INV' AND RefNbr = '003223')
	OR (TranType = 'INV' AND RefNbr = '003224')
	OR (TranType = 'INV' AND RefNbr = '003225')
	OR (TranType = 'INV' AND RefNbr = '003226')
	OR (TranType = 'INV' AND RefNbr = '003227')
	OR (TranType = 'QCK' AND RefNbr = '001318')
	OR (TranType = 'QCK' AND RefNbr = '001319')
	OR (TranType = 'QCK' AND RefNbr = '001320')
	OR (TranType = 'QCK' AND RefNbr = '001321')
	OR (TranType = 'QCK' AND RefNbr = '001339')
	OR (TranType = 'QCK' AND RefNbr = '001564')
	OR (TranType = 'QCK' AND RefNbr = '001565')
	OR (TranType = 'QCK' AND RefNbr = '001566')
	OR (TranType = 'QCK' AND RefNbr = '001661')
	OR (TranType = 'QCK' AND RefNbr = '001711')
	OR (TranType = 'QCK' AND RefNbr = '001819')
	OR (TranType = 'QCK' AND RefNbr = '002013')
	OR (TranType = 'QCK' AND RefNbr = '002036')
)
```

VALUES
```sql
SELECT
	T.*
FROM APTran T
INNER JOIN
(
	VALUES
		('ACR', '000417'),
		('ADR', '000400'),
		('ADR', '000418'),
		('ADR', '000419'),
		('ADR', '001261'),
		('ADR', '001262'),
		('ADR', '001577'),
		('ADR', '001578'),
		('ADR', '002001'),
		('ADR', '002043'),
		('ADR', '002455'),
		('ADR', '002456'),
		('ADR', '002457'),
		('INV', '000183'),
		('INV', '000184'),
		('INV', '000185'),
		('INV', '000186'),
		('INV', '000187'),
		('INV', '000188'),
		('INV', '000189'),
		('INV', '000190'),
		('INV', '000191'),
		('INV', '000192'),
		('INV', '000193'),
		('INV', '000194'),
		('INV', '000195'),
		('INV', '000196'),
		('INV', '000197'),
		('INV', '000198'),
		('INV', '000199'),
		('INV', '000200'),
		('INV', '000201'),
		('INV', '000202'),
		('INV', '000203'),
		('INV', '000204'),
		('INV', '000205'),
		('INV', '000206'),
		('INV', '000207'),
		('INV', '000208'),
		('INV', '000209'),
		('INV', '000210'),
		('INV', '000211'),
		('INV', '000212'),
		('INV', '000213'),
		('INV', '000214'),
		('INV', '000215'),
		('INV', '000216'),
		('INV', '000217'),
		('INV', '000218'),
		('INV', '000219'),
		('INV', '000220'),
		('INV', '000221'),
		('INV', '000222'),
		('INV', '000223'),
		('INV', '000224'),
		('INV', '000225'),
		('INV', '000226'),
		('INV', '000227'),
		('INV', '000228'),
		('INV', '000229'),
		('INV', '000230'),
		('INV', '000231'),
		('INV', '000232'),
		('INV', '000233'),
		('INV', '000234'),
		('INV', '000235'),
		('INV', '000236'),
		('INV', '000237'),
		('INV', '000238'),
		('INV', '000239'),
		('INV', '000240'),
		('INV', '000241'),
		('INV', '000242'),
		('INV', '000243'),
		('INV', '000244'),
		('INV', '000245'),
		('INV', '000246'),
		('INV', '000247'),
		('INV', '000248'),
		('INV', '000249'),
		('INV', '000250'),
		('INV', '000251'),
		('INV', '000252'),
		('INV', '000253'),
		('INV', '000254'),
		('INV', '000255'),
		('INV', '000256'),
		('INV', '000257'),
		('INV', '000258'),
		('INV', '000259'),
		('INV', '000260'),
		('INV', '000261'),
		('INV', '000262'),
		('INV', '000263'),
		('INV', '000264'),
		('INV', '000265'),
		('INV', '000266'),
		('INV', '000267'),
		('INV', '000268'),
		('INV', '000269'),
		('INV', '000270'),
		('INV', '000271'),
		('INV', '000272'),
		('INV', '000273'),
		('INV', '000274'),
		('INV', '000275'),
		('INV', '000276'),
		('INV', '000277'),
		('INV', '000278'),
		('INV', '000279'),
		('INV', '000280'),
		('INV', '000281'),
		('INV', '000282'),
		('INV', '000283'),
		('INV', '000284'),
		('INV', '000285'),
		('INV', '000286'),
		('INV', '000287'),
		('INV', '000288'),
		('INV', '000289'),
		('INV', '000290'),
		('INV', '000291'),
		('INV', '000292'),
		('INV', '000293'),
		('INV', '000294'),
		('INV', '000295'),
		('INV', '000296'),
		('INV', '000297'),
		('INV', '000298'),
		('INV', '000299'),
		('INV', '000300'),
		('INV', '000301'),
		('INV', '000302'),
		('INV', '000303'),
		('INV', '000304'),
		('INV', '000305'),
		('INV', '000306'),
		('INV', '000307'),
		('INV', '000308'),
		('INV', '000309'),
		('INV', '000310'),
		('INV', '000311'),
		('INV', '000312'),
		('INV', '000313'),
		('INV', '000314'),
		('INV', '000315'),
		('INV', '000316'),
		('INV', '000317'),
		('INV', '000318'),
		('INV', '000319'),
		('INV', '000320'),
		('INV', '000321'),
		('INV', '000322'),
		('INV', '000323'),
		('INV', '000324'),
		('INV', '000325'),
		('INV', '000326'),
		('INV', '000327'),
		('INV', '000328'),
		('INV', '000329'),
		('INV', '000330'),
		('INV', '000331'),
		('INV', '000332'),
		('INV', '000333'),
		('INV', '000334'),
		('INV', '000335'),
		('INV', '000336'),
		('INV', '000337'),
		('INV', '000338'),
		('INV', '000339'),
		('INV', '000341'),
		('INV', '000342'),
		('INV', '000343'),
		('INV', '000344'),
		('INV', '000345'),
		('INV', '000346'),
		('INV', '000347'),
		('INV', '000348'),
		('INV', '000349'),
		('INV', '000350'),
		('INV', '000351'),
		('INV', '000352'),
		('INV', '000353'),
		('INV', '000354'),
		('INV', '000355'),
		('INV', '000356'),
		('INV', '000357'),
		('INV', '000358'),
		('INV', '000359'),
		('INV', '000360'),
		('INV', '000361'),
		('INV', '000362'),
		('INV', '000363'),
		('INV', '000364'),
		('INV', '000365'),
		('INV', '000366'),
		('INV', '000367'),
		('INV', '000368'),
		('INV', '000369'),
		('INV', '000370'),
		('INV', '000371'),
		('INV', '000372'),
		('INV', '000373'),
		('INV', '000374'),
		('INV', '000375'),
		('INV', '000376'),
		('INV', '000377'),
		('INV', '000378'),
		('INV', '000379'),
		('INV', '000380'),
		('INV', '000381'),
		('INV', '000382'),
		('INV', '000383'),
		('INV', '000384'),
		('INV', '000385'),
		('INV', '000386'),
		('INV', '000387'),
		('INV', '000388'),
		('INV', '000389'),
		('INV', '000390'),
		('INV', '000391'),
		('INV', '000392'),
		('INV', '000393'),
		('INV', '000394'),
		('INV', '000395'),
		('INV', '000396'),
		('INV', '000397'),
		('INV', '000398'),
		('INV', '000399'),
		('INV', '000401'),
		('INV', '000402'),
		('INV', '000403'),
		('INV', '000404'),
		('INV', '000405'),
		('INV', '000406'),
		('INV', '000407'),
		('INV', '000408'),
		('INV', '000409'),
		('INV', '000410'),
		('INV', '000411'),
		('INV', '000412'),
		('INV', '000413'),
		('INV', '000414'),
		('INV', '000415'),
		('INV', '000416'),
		('INV', '000420'),
		('INV', '000421'),
		('INV', '000422'),
		('INV', '000423'),
		('INV', '000424'),
		('INV', '000425'),
		('INV', '000426'),
		('INV', '000427'),
		('INV', '000428'),
		('INV', '000429'),
		('INV', '000430'),
		('INV', '000431'),
		('INV', '000432'),
		('INV', '000433'),
		('INV', '000434'),
		('INV', '000435'),
		('INV', '000436'),
		('INV', '000437'),
		('INV', '000438'),
		('INV', '000439'),
		('INV', '000440'),
		('INV', '000441'),
		('INV', '000442'),
		('INV', '000443'),
		('INV', '000444'),
		('INV', '000446'),
		('INV', '000447'),
		('INV', '000448'),
		('INV', '000449'),
		('INV', '000450'),
		('INV', '000451'),
		('INV', '000452'),
		('INV', '000453'),
		('INV', '000454'),
		('INV', '000455'),
		('INV', '000456'),
		('INV', '000457'),
		('INV', '000458'),
		('INV', '000459'),
		('INV', '000460'),
		('INV', '000461'),
		('INV', '000462'),
		('INV', '000463'),
		('INV', '000464'),
		('INV', '000465'),
		('INV', '000466'),
		('INV', '000467'),
		('INV', '000468'),
		('INV', '000469'),
		('INV', '000470'),
		('INV', '000471'),
		('INV', '000472'),
		('INV', '000473'),
		('INV', '000474'),
		('INV', '000475'),
		('INV', '000476'),
		('INV', '000477'),
		('INV', '000478'),
		('INV', '000479'),
		('INV', '000480'),
		('INV', '000481'),
		('INV', '000482'),
		('INV', '000483'),
		('INV', '000484'),
		('INV', '000485'),
		('INV', '000486'),
		('INV', '000487'),
		('INV', '000488'),
		('INV', '000489'),
		('INV', '000490'),
		('INV', '000491'),
		('INV', '000492'),
		('INV', '000493'),
		('INV', '000494'),
		('INV', '000495'),
		('INV', '000496'),
		('INV', '000497'),
		('INV', '000498'),
		('INV', '000499'),
		('INV', '000500'),
		('INV', '000501'),
		('INV', '000502'),
		('INV', '000503'),
		('INV', '000504'),
		('INV', '000505'),
		('INV', '000506'),
		('INV', '000507'),
		('INV', '000508'),
		('INV', '000509'),
		('INV', '000510'),
		('INV', '000511'),
		('INV', '000512'),
		('INV', '000513'),
		('INV', '000514'),
		('INV', '000515'),
		('INV', '000516'),
		('INV', '000517'),
		('INV', '000518'),
		('INV', '000519'),
		('INV', '000520'),
		('INV', '000521'),
		('INV', '000522'),
		('INV', '000523'),
		('INV', '000524'),
		('INV', '000525'),
		('INV', '000526'),
		('INV', '000527'),
		('INV', '000528'),
		('INV', '000529'),
		('INV', '000530'),
		('INV', '000531'),
		('INV', '000532'),
		('INV', '000533'),
		('INV', '000534'),
		('INV', '000535'),
		('INV', '000536'),
		('INV', '000537'),
		('INV', '000538'),
		('INV', '000539'),
		('INV', '000540'),
		('INV', '000541'),
		('INV', '000542'),
		('INV', '000543'),
		('INV', '000544'),
		('INV', '000545'),
		('INV', '000546'),
		('INV', '000547'),
		('INV', '000548'),
		('INV', '000549'),
		('INV', '000550'),
		('INV', '000551'),
		('INV', '000552'),
		('INV', '000553'),
		('INV', '000554'),
		('INV', '000555'),
		('INV', '000556'),
		('INV', '000557'),
		('INV', '000558'),
		('INV', '000559'),
		('INV', '000560'),
		('INV', '000561'),
		('INV', '000562'),
		('INV', '000563'),
		('INV', '000564'),
		('INV', '000565'),
		('INV', '000566'),
		('INV', '000567'),
		('INV', '000568'),
		('INV', '000569'),
		('INV', '000570'),
		('INV', '000571'),
		('INV', '000572'),
		('INV', '000573'),
		('INV', '000574'),
		('INV', '000575'),
		('INV', '000576'),
		('INV', '000577'),
		('INV', '000578'),
		('INV', '000579'),
		('INV', '000580'),
		('INV', '000581'),
		('INV', '000582'),
		('INV', '000583'),
		('INV', '000584'),
		('INV', '000585'),
		('INV', '000586'),
		('INV', '000587'),
		('INV', '000588'),
		('INV', '000589'),
		('INV', '000590'),
		('INV', '000591'),
		('INV', '000592'),
		('INV', '000593'),
		('INV', '000594'),
		('INV', '000595'),
		('INV', '000596'),
		('INV', '000597'),
		('INV', '000598'),
		('INV', '000599'),
		('INV', '000600'),
		('INV', '000601'),
		('INV', '000602'),
		('INV', '000603'),
		('INV', '000604'),
		('INV', '000605'),
		('INV', '000606'),
		('INV', '000607'),
		('INV', '000608'),
		('INV', '000609'),
		('INV', '000610'),
		('INV', '000611'),
		('INV', '000612'),
		('INV', '000613'),
		('INV', '000614'),
		('INV', '000615'),
		('INV', '000616'),
		('INV', '000617'),
		('INV', '000618'),
		('INV', '000619'),
		('INV', '000620'),
		('INV', '000621'),
		('INV', '000622'),
		('INV', '000623'),
		('INV', '000624'),
		('INV', '000625'),
		('INV', '000626'),
		('INV', '000627'),
		('INV', '000628'),
		('INV', '000629'),
		('INV', '000630'),
		('INV', '000632'),
		('INV', '000633'),
		('INV', '000634'),
		('INV', '000635'),
		('INV', '000636'),
		('INV', '000637'),
		('INV', '000638'),
		('INV', '000639'),
		('INV', '000640'),
		('INV', '000641'),
		('INV', '000642'),
		('INV', '000643'),
		('INV', '000644'),
		('INV', '000645'),
		('INV', '000646'),
		('INV', '000647'),
		('INV', '000648'),
		('INV', '000649'),
		('INV', '000651'),
		('INV', '000652'),
		('INV', '000653'),
		('INV', '000654'),
		('INV', '000655'),
		('INV', '000656'),
		('INV', '000657'),
		('INV', '000658'),
		('INV', '000659'),
		('INV', '000660'),
		('INV', '000661'),
		('INV', '000662'),
		('INV', '000663'),
		('INV', '000664'),
		('INV', '000665'),
		('INV', '000666'),
		('INV', '000667'),
		('INV', '000668'),
		('INV', '000669'),
		('INV', '000670'),
		('INV', '000671'),
		('INV', '000672'),
		('INV', '000673'),
		('INV', '000674'),
		('INV', '000675'),
		('INV', '000676'),
		('INV', '000677'),
		('INV', '000678'),
		('INV', '000679'),
		('INV', '000680'),
		('INV', '000681'),
		('INV', '000682'),
		('INV', '000683'),
		('INV', '000684'),
		('INV', '000685'),
		('INV', '000686'),
		('INV', '000687'),
		('INV', '000688'),
		('INV', '000689'),
		('INV', '000690'),
		('INV', '000691'),
		('INV', '000692'),
		('INV', '000693'),
		('INV', '000694'),
		('INV', '000695'),
		('INV', '000696'),
		('INV', '000697'),
		('INV', '000698'),
		('INV', '000699'),
		('INV', '000700'),
		('INV', '000701'),
		('INV', '000702'),
		('INV', '000703'),
		('INV', '000704'),
		('INV', '000705'),
		('INV', '000706'),
		('INV', '000707'),
		('INV', '000708'),
		('INV', '000709'),
		('INV', '000710'),
		('INV', '000711'),
		('INV', '000712'),
		('INV', '000713'),
		('INV', '000714'),
		('INV', '000715'),
		('INV', '000716'),
		('INV', '000717'),
		('INV', '000718'),
		('INV', '000719'),
		('INV', '000720'),
		('INV', '000721'),
		('INV', '000722'),
		('INV', '000723'),
		('INV', '000724'),
		('INV', '000725'),
		('INV', '000726'),
		('INV', '000727'),
		('INV', '000728'),
		('INV', '000729'),
		('INV', '000730'),
		('INV', '000731'),
		('INV', '000732'),
		('INV', '000733'),
		('INV', '000734'),
		('INV', '000735'),
		('INV', '000736'),
		('INV', '000737'),
		('INV', '000738'),
		('INV', '000739'),
		('INV', '000740'),
		('INV', '000741'),
		('INV', '000742'),
		('INV', '000743'),
		('INV', '000744'),
		('INV', '000745'),
		('INV', '000746'),
		('INV', '000747'),
		('INV', '000748'),
		('INV', '000749'),
		('INV', '000750'),
		('INV', '000751'),
		('INV', '000752'),
		('INV', '000753'),
		('INV', '000754'),
		('INV', '000755'),
		('INV', '000756'),
		('INV', '000757'),
		('INV', '000758'),
		('INV', '000759'),
		('INV', '000760'),
		('INV', '000761'),
		('INV', '000762'),
		('INV', '000763'),
		('INV', '000764'),
		('INV', '000765'),
		('INV', '000766'),
		('INV', '000767'),
		('INV', '000768'),
		('INV', '000769'),
		('INV', '000770'),
		('INV', '000771'),
		('INV', '000772'),
		('INV', '000773'),
		('INV', '000774'),
		('INV', '000775'),
		('INV', '000776'),
		('INV', '000777'),
		('INV', '000778'),
		('INV', '000779'),
		('INV', '000780'),
		('INV', '000781'),
		('INV', '000782'),
		('INV', '000783'),
		('INV', '000784'),
		('INV', '000785'),
		('INV', '000786'),
		('INV', '000787'),
		('INV', '000788'),
		('INV', '000789'),
		('INV', '000790'),
		('INV', '000791'),
		('INV', '000792'),
		('INV', '000793'),
		('INV', '000794'),
		('INV', '000795'),
		('INV', '000796'),
		('INV', '000797'),
		('INV', '000798'),
		('INV', '000799'),
		('INV', '000800'),
		('INV', '000801'),
		('INV', '000802'),
		('INV', '000803'),
		('INV', '000804'),
		('INV', '000805'),
		('INV', '000806'),
		('INV', '000807'),
		('INV', '000808'),
		('INV', '000809'),
		('INV', '000810'),
		('INV', '000811'),
		('INV', '000812'),
		('INV', '000813'),
		('INV', '000814'),
		('INV', '000815'),
		('INV', '000816'),
		('INV', '000817'),
		('INV', '000818'),
		('INV', '000819'),
		('INV', '000820'),
		('INV', '000821'),
		('INV', '000822'),
		('INV', '000823'),
		('INV', '000824'),
		('INV', '000825'),
		('INV', '000826'),
		('INV', '000827'),
		('INV', '000828'),
		('INV', '000829'),
		('INV', '000830'),
		('INV', '000831'),
		('INV', '000832'),
		('INV', '000833'),
		('INV', '000834'),
		('INV', '000835'),
		('INV', '000836'),
		('INV', '000837'),
		('INV', '000838'),
		('INV', '000839'),
		('INV', '000840'),
		('INV', '000841'),
		('INV', '000842'),
		('INV', '000843'),
		('INV', '000844'),
		('INV', '000845'),
		('INV', '000846'),
		('INV', '000847'),
		('INV', '000848'),
		('INV', '000849'),
		('INV', '000850'),
		('INV', '000851'),
		('INV', '000852'),
		('INV', '000853'),
		('INV', '000854'),
		('INV', '000855'),
		('INV', '000856'),
		('INV', '000857'),
		('INV', '000858'),
		('INV', '000859'),
		('INV', '000860'),
		('INV', '000861'),
		('INV', '000862'),
		('INV', '000863'),
		('INV', '000864'),
		('INV', '000865'),
		('INV', '000866'),
		('INV', '000867'),
		('INV', '000868'),
		('INV', '000869'),
		('INV', '000870'),
		('INV', '000871'),
		('INV', '000872'),
		('INV', '000873'),
		('INV', '000874'),
		('INV', '000875'),
		('INV', '000876'),
		('INV', '000877'),
		('INV', '000878'),
		('INV', '000879'),
		('INV', '000880'),
		('INV', '000881'),
		('INV', '000882'),
		('INV', '000883'),
		('INV', '000884'),
		('INV', '000885'),
		('INV', '000886'),
		('INV', '000887'),
		('INV', '000888'),
		('INV', '000889'),
		('INV', '000890'),
		('INV', '000891'),
		('INV', '000892'),
		('INV', '000893'),
		('INV', '000894'),
		('INV', '000895'),
		('INV', '000896'),
		('INV', '000897'),
		('INV', '000898'),
		('INV', '000899'),
		('INV', '000900'),
		('INV', '000901'),
		('INV', '000902'),
		('INV', '000903'),
		('INV', '000904'),
		('INV', '000905'),
		('INV', '000906'),
		('INV', '000907'),
		('INV', '000908'),
		('INV', '000909'),
		('INV', '000910'),
		('INV', '000911'),
		('INV', '000912'),
		('INV', '000913'),
		('INV', '000914'),
		('INV', '000915'),
		('INV', '000916'),
		('INV', '000917'),
		('INV', '000918'),
		('INV', '000919'),
		('INV', '000920'),
		('INV', '000921'),
		('INV', '000922'),
		('INV', '000923'),
		('INV', '000924'),
		('INV', '000925'),
		('INV', '000926'),
		('INV', '000927'),
		('INV', '000928'),
		('INV', '000929'),
		('INV', '000930'),
		('INV', '000931'),
		('INV', '000932'),
		('INV', '000933'),
		('INV', '000934'),
		('INV', '000935'),
		('INV', '000936'),
		('INV', '000937'),
		('INV', '000938'),
		('INV', '000939'),
		('INV', '000940'),
		('INV', '000941'),
		('INV', '000942'),
		('INV', '000943'),
		('INV', '000944'),
		('INV', '000945'),
		('INV', '000946'),
		('INV', '000947'),
		('INV', '000948'),
		('INV', '000949'),
		('INV', '000950'),
		('INV', '000951'),
		('INV', '000952'),
		('INV', '000953'),
		('INV', '000954'),
		('INV', '000955'),
		('INV', '000956'),
		('INV', '000957'),
		('INV', '000958'),
		('INV', '000959'),
		('INV', '000960'),
		('INV', '000961'),
		('INV', '000962'),
		('INV', '000963'),
		('INV', '000964'),
		('INV', '000965'),
		('INV', '000966'),
		('INV', '000967'),
		('INV', '000968'),
		('INV', '000969'),
		('INV', '000970'),
		('INV', '000971'),
		('INV', '000972'),
		('INV', '000973'),
		('INV', '000974'),
		('INV', '000975'),
		('INV', '000976'),
		('INV', '000977'),
		('INV', '000978'),
		('INV', '000979'),
		('INV', '000980'),
		('INV', '000981'),
		('INV', '000982'),
		('INV', '000983'),
		('INV', '000984'),
		('INV', '000985'),
		('INV', '000986'),
		('INV', '000987'),
		('INV', '000988'),
		('INV', '000989'),
		('INV', '000990'),
		('INV', '000991'),
		('INV', '000992'),
		('INV', '000993'),
		('INV', '000994'),
		('INV', '000995'),
		('INV', '000996'),
		('INV', '000997'),
		('INV', '000998'),
		('INV', '000999'),
		('INV', '001000'),
		('INV', '001001'),
		('INV', '001002'),
		('INV', '001003'),
		('INV', '001004'),
		('INV', '001005'),
		('INV', '001006'),
		('INV', '001007'),
		('INV', '001008'),
		('INV', '001009'),
		('INV', '001010'),
		('INV', '001011'),
		('INV', '001012'),
		('INV', '001013'),
		('INV', '001014'),
		('INV', '001015'),
		('INV', '001016'),
		('INV', '001017'),
		('INV', '001018'),
		('INV', '001019'),
		('INV', '001020'),
		('INV', '001021'),
		('INV', '001022'),
		('INV', '001023'),
		('INV', '001024'),
		('INV', '001025'),
		('INV', '001026'),
		('INV', '001027'),
		('INV', '001028'),
		('INV', '001029'),
		('INV', '001030'),
		('INV', '001031'),
		('INV', '001032'),
		('INV', '001033'),
		('INV', '001034'),
		('INV', '001035'),
		('INV', '001036'),
		('INV', '001037'),
		('INV', '001038'),
		('INV', '001039'),
		('INV', '001040'),
		('INV', '001041'),
		('INV', '001042'),
		('INV', '001043'),
		('INV', '001044'),
		('INV', '001045'),
		('INV', '001046'),
		('INV', '001047'),
		('INV', '001048'),
		('INV', '001049'),
		('INV', '001050'),
		('INV', '001051'),
		('INV', '001052'),
		('INV', '001053'),
		('INV', '001054'),
		('INV', '001055'),
		('INV', '001056'),
		('INV', '001057'),
		('INV', '001058'),
		('INV', '001059'),
		('INV', '001060'),
		('INV', '001061'),
		('INV', '001062'),
		('INV', '001063'),
		('INV', '001064'),
		('INV', '001065'),
		('INV', '001066'),
		('INV', '001067'),
		('INV', '001068'),
		('INV', '001069'),
		('INV', '001070'),
		('INV', '001071'),
		('INV', '001072'),
		('INV', '001073'),
		('INV', '001074'),
		('INV', '001075'),
		('INV', '001076'),
		('INV', '001077'),
		('INV', '001078'),
		('INV', '001079'),
		('INV', '001080'),
		('INV', '001081'),
		('INV', '001082'),
		('INV', '001083'),
		('INV', '001084'),
		('INV', '001085'),
		('INV', '001086'),
		('INV', '001087'),
		('INV', '001088'),
		('INV', '001089'),
		('INV', '001090'),
		('INV', '001091'),
		('INV', '001092'),
		('INV', '001093'),
		('INV', '001094'),
		('INV', '001095'),
		('INV', '001096'),
		('INV', '001097'),
		('INV', '001098'),
		('INV', '001099'),
		('INV', '001100'),
		('INV', '001101'),
		('INV', '001102'),
		('INV', '001103'),
		('INV', '001104'),
		('INV', '001105'),
		('INV', '001106'),
		('INV', '001107'),
		('INV', '001108'),
		('INV', '001109'),
		('INV', '001110'),
		('INV', '001111'),
		('INV', '001112'),
		('INV', '001113'),
		('INV', '001114'),
		('INV', '001115'),
		('INV', '001116'),
		('INV', '001117'),
		('INV', '001118'),
		('INV', '001119'),
		('INV', '001120'),
		('INV', '001121'),
		('INV', '001122'),
		('INV', '001123'),
		('INV', '001124'),
		('INV', '001125'),
		('INV', '001126'),
		('INV', '001127'),
		('INV', '001128'),
		('INV', '001129'),
		('INV', '001130'),
		('INV', '001131'),
		('INV', '001132'),
		('INV', '001133'),
		('INV', '001134'),
		('INV', '001135'),
		('INV', '001136'),
		('INV', '001137'),
		('INV', '001138'),
		('INV', '001139'),
		('INV', '001140'),
		('INV', '001141'),
		('INV', '001142'),
		('INV', '001143'),
		('INV', '001144'),
		('INV', '001145'),
		('INV', '001146'),
		('INV', '001147'),
		('INV', '001148'),
		('INV', '001149'),
		('INV', '001150'),
		('INV', '001151'),
		('INV', '001152'),
		('INV', '001153'),
		('INV', '001154'),
		('INV', '001155'),
		('INV', '001156'),
		('INV', '001157'),
		('INV', '001158'),
		('INV', '001159'),
		('INV', '001160'),
		('INV', '001161'),
		('INV', '001162'),
		('INV', '001163'),
		('INV', '001164'),
		('INV', '001165'),
		('INV', '001166'),
		('INV', '001167'),
		('INV', '001168'),
		('INV', '001169'),
		('INV', '001170'),
		('INV', '001171'),
		('INV', '001172'),
		('INV', '001173'),
		('INV', '001174'),
		('INV', '001175'),
		('INV', '001176'),
		('INV', '001177'),
		('INV', '001178'),
		('INV', '001179'),
		('INV', '001180'),
		('INV', '001181'),
		('INV', '001182'),
		('INV', '001183'),
		('INV', '001184'),
		('INV', '001185'),
		('INV', '001186'),
		('INV', '001187'),
		('INV', '001188'),
		('INV', '001189'),
		('INV', '001190'),
		('INV', '001191'),
		('INV', '001192'),
		('INV', '001193'),
		('INV', '001194'),
		('INV', '001195'),
		('INV', '001196'),
		('INV', '001197'),
		('INV', '001198'),
		('INV', '001199'),
		('INV', '001200'),
		('INV', '001201'),
		('INV', '001202'),
		('INV', '001203'),
		('INV', '001204'),
		('INV', '001205'),
		('INV', '001206'),
		('INV', '001207'),
		('INV', '001208'),
		('INV', '001209'),
		('INV', '001210'),
		('INV', '001211'),
		('INV', '001212'),
		('INV', '001213'),
		('INV', '001214'),
		('INV', '001215'),
		('INV', '001216'),
		('INV', '001217'),
		('INV', '001218'),
		('INV', '001219'),
		('INV', '001220'),
		('INV', '001221'),
		('INV', '001222'),
		('INV', '001223'),
		('INV', '001224'),
		('INV', '001225'),
		('INV', '001226'),
		('INV', '001227'),
		('INV', '001228'),
		('INV', '001229'),
		('INV', '001230'),
		('INV', '001231'),
		('INV', '001232'),
		('INV', '001233'),
		('INV', '001234'),
		('INV', '001235'),
		('INV', '001236'),
		('INV', '001237'),
		('INV', '001238'),
		('INV', '001239'),
		('INV', '001240'),
		('INV', '001241'),
		('INV', '001242'),
		('INV', '001243'),
		('INV', '001244'),
		('INV', '001245'),
		('INV', '001246'),
		('INV', '001247'),
		('INV', '001248'),
		('INV', '001249'),
		('INV', '001250'),
		('INV', '001251'),
		('INV', '001252'),
		('INV', '001253'),
		('INV', '001254'),
		('INV', '001255'),
		('INV', '001256'),
		('INV', '001257'),
		('INV', '001258'),
		('INV', '001259'),
		('INV', '001260'),
		('INV', '001263'),
		('INV', '001264'),
		('INV', '001265'),
		('INV', '001266'),
		('INV', '001267'),
		('INV', '001268'),
		('INV', '001269'),
		('INV', '001270'),
		('INV', '001271'),
		('INV', '001272'),
		('INV', '001273'),
		('INV', '001274'),
		('INV', '001275'),
		('INV', '001276'),
		('INV', '001277'),
		('INV', '001278'),
		('INV', '001279'),
		('INV', '001280'),
		('INV', '001281'),
		('INV', '001282'),
		('INV', '001283'),
		('INV', '001284'),
		('INV', '001285'),
		('INV', '001286'),
		('INV', '001287'),
		('INV', '001288'),
		('INV', '001289'),
		('INV', '001290'),
		('INV', '001291'),
		('INV', '001292'),
		('INV', '001293'),
		('INV', '001294'),
		('INV', '001295'),
		('INV', '001296'),
		('INV', '001297'),
		('INV', '001298'),
		('INV', '001299'),
		('INV', '001300'),
		('INV', '001301'),
		('INV', '001302'),
		('INV', '001303'),
		('INV', '001304'),
		('INV', '001305'),
		('INV', '001306'),
		('INV', '001307'),
		('INV', '001308'),
		('INV', '001309'),
		('INV', '001310'),
		('INV', '001311'),
		('INV', '001312'),
		('INV', '001313'),
		('INV', '001314'),
		('INV', '001315'),
		('INV', '001316'),
		('INV', '001317'),
		('INV', '001318'),
		('INV', '001319'),
		('INV', '001320'),
		('INV', '001321'),
		('INV', '001322'),
		('INV', '001323'),
		('INV', '001324'),
		('INV', '001325'),
		('INV', '001326'),
		('INV', '001327'),
		('INV', '001328'),
		('INV', '001329'),
		('INV', '001330'),
		('INV', '001331'),
		('INV', '001332'),
		('INV', '001333'),
		('INV', '001334'),
		('INV', '001335'),
		('INV', '001336'),
		('INV', '001337'),
		('INV', '001338'),
		('INV', '001339'),
		('INV', '001340'),
		('INV', '001341'),
		('INV', '001342'),
		('INV', '001343'),
		('INV', '001344'),
		('INV', '001345'),
		('INV', '001346'),
		('INV', '001347'),
		('INV', '001348'),
		('INV', '001349'),
		('INV', '001350'),
		('INV', '001351'),
		('INV', '001352'),
		('INV', '001353'),
		('INV', '001354'),
		('INV', '001355'),
		('INV', '001356'),
		('INV', '001357'),
		('INV', '001358'),
		('INV', '001359'),
		('INV', '001360'),
		('INV', '001361'),
		('INV', '001362'),
		('INV', '001363'),
		('INV', '001364'),
		('INV', '001365'),
		('INV', '001366'),
		('INV', '001367'),
		('INV', '001368'),
		('INV', '001369'),
		('INV', '001370'),
		('INV', '001371'),
		('INV', '001372'),
		('INV', '001373'),
		('INV', '001374'),
		('INV', '001375'),
		('INV', '001376'),
		('INV', '001377'),
		('INV', '001378'),
		('INV', '001379'),
		('INV', '001380'),
		('INV', '001381'),
		('INV', '001382'),
		('INV', '001383'),
		('INV', '001384'),
		('INV', '001385'),
		('INV', '001386'),
		('INV', '001387'),
		('INV', '001388'),
		('INV', '001389'),
		('INV', '001390'),
		('INV', '001391'),
		('INV', '001392'),
		('INV', '001393'),
		('INV', '001394'),
		('INV', '001395'),
		('INV', '001396'),
		('INV', '001397'),
		('INV', '001398'),
		('INV', '001399'),
		('INV', '001400'),
		('INV', '001401'),
		('INV', '001402'),
		('INV', '001403'),
		('INV', '001404'),
		('INV', '001405'),
		('INV', '001406'),
		('INV', '001407'),
		('INV', '001408'),
		('INV', '001409'),
		('INV', '001410'),
		('INV', '001411'),
		('INV', '001412'),
		('INV', '001413'),
		('INV', '001414'),
		('INV', '001415'),
		('INV', '001416'),
		('INV', '001417'),
		('INV', '001418'),
		('INV', '001419'),
		('INV', '001420'),
		('INV', '001421'),
		('INV', '001422'),
		('INV', '001423'),
		('INV', '001424'),
		('INV', '001425'),
		('INV', '001426'),
		('INV', '001427'),
		('INV', '001428'),
		('INV', '001429'),
		('INV', '001430'),
		('INV', '001431'),
		('INV', '001432'),
		('INV', '001433'),
		('INV', '001434'),
		('INV', '001435'),
		('INV', '001436'),
		('INV', '001437'),
		('INV', '001438'),
		('INV', '001439'),
		('INV', '001440'),
		('INV', '001441'),
		('INV', '001442'),
		('INV', '001443'),
		('INV', '001444'),
		('INV', '001445'),
		('INV', '001446'),
		('INV', '001447'),
		('INV', '001448'),
		('INV', '001449'),
		('INV', '001450'),
		('INV', '001451'),
		('INV', '001452'),
		('INV', '001453'),
		('INV', '001454'),
		('INV', '001455'),
		('INV', '001456'),
		('INV', '001457'),
		('INV', '001458'),
		('INV', '001459'),
		('INV', '001460'),
		('INV', '001461'),
		('INV', '001462'),
		('INV', '001463'),
		('INV', '001464'),
		('INV', '001465'),
		('INV', '001466'),
		('INV', '001467'),
		('INV', '001468'),
		('INV', '001469'),
		('INV', '001470'),
		('INV', '001471'),
		('INV', '001472'),
		('INV', '001473'),
		('INV', '001474'),
		('INV', '001475'),
		('INV', '001476'),
		('INV', '001477'),
		('INV', '001478'),
		('INV', '001479'),
		('INV', '001480'),
		('INV', '001481'),
		('INV', '001482'),
		('INV', '001483'),
		('INV', '001484'),
		('INV', '001485'),
		('INV', '001486'),
		('INV', '001487'),
		('INV', '001488'),
		('INV', '001489'),
		('INV', '001490'),
		('INV', '001491'),
		('INV', '001492'),
		('INV', '001493'),
		('INV', '001494'),
		('INV', '001495'),
		('INV', '001496'),
		('INV', '001497'),
		('INV', '001498'),
		('INV', '001499'),
		('INV', '001500'),
		('INV', '001501'),
		('INV', '001502'),
		('INV', '001503'),
		('INV', '001504'),
		('INV', '001505'),
		('INV', '001506'),
		('INV', '001507'),
		('INV', '001508'),
		('INV', '001509'),
		('INV', '001510'),
		('INV', '001511'),
		('INV', '001512'),
		('INV', '001513'),
		('INV', '001514'),
		('INV', '001515'),
		('INV', '001516'),
		('INV', '001517'),
		('INV', '001518'),
		('INV', '001519'),
		('INV', '001520'),
		('INV', '001521'),
		('INV', '001522'),
		('INV', '001523'),
		('INV', '001524'),
		('INV', '001525'),
		('INV', '001526'),
		('INV', '001527'),
		('INV', '001528'),
		('INV', '001529'),
		('INV', '001530'),
		('INV', '001531'),
		('INV', '001532'),
		('INV', '001533'),
		('INV', '001534'),
		('INV', '001535'),
		('INV', '001536'),
		('INV', '001537'),
		('INV', '001538'),
		('INV', '001539'),
		('INV', '001540'),
		('INV', '001541'),
		('INV', '001542'),
		('INV', '001543'),
		('INV', '001544'),
		('INV', '001545'),
		('INV', '001546'),
		('INV', '001547'),
		('INV', '001548'),
		('INV', '001549'),
		('INV', '001550'),
		('INV', '001551'),
		('INV', '001552'),
		('INV', '001553'),
		('INV', '001554'),
		('INV', '001555'),
		('INV', '001556'),
		('INV', '001557'),
		('INV', '001558'),
		('INV', '001559'),
		('INV', '001560'),
		('INV', '001561'),
		('INV', '001562'),
		('INV', '001563'),
		('INV', '001564'),
		('INV', '001565'),
		('INV', '001566'),
		('INV', '001567'),
		('INV', '001568'),
		('INV', '001569'),
		('INV', '001570'),
		('INV', '001571'),
		('INV', '001572'),
		('INV', '001573'),
		('INV', '001574'),
		('INV', '001575'),
		('INV', '001576'),
		('INV', '001579'),
		('INV', '001580'),
		('INV', '001581'),
		('INV', '001582'),
		('INV', '001583'),
		('INV', '001584'),
		('INV', '001585'),
		('INV', '001586'),
		('INV', '001587'),
		('INV', '001588'),
		('INV', '001589'),
		('INV', '001590'),
		('INV', '001591'),
		('INV', '001592'),
		('INV', '001593'),
		('INV', '001594'),
		('INV', '001595'),
		('INV', '001596'),
		('INV', '001597'),
		('INV', '001598'),
		('INV', '001599'),
		('INV', '001600'),
		('INV', '001601'),
		('INV', '001602'),
		('INV', '001603'),
		('INV', '001604'),
		('INV', '001605'),
		('INV', '001606'),
		('INV', '001607'),
		('INV', '001608'),
		('INV', '001609'),
		('INV', '001610'),
		('INV', '001611'),
		('INV', '001612'),
		('INV', '001613'),
		('INV', '001614'),
		('INV', '001615'),
		('INV', '001616'),
		('INV', '001617'),
		('INV', '001618'),
		('INV', '001619'),
		('INV', '001620'),
		('INV', '001621'),
		('INV', '001622'),
		('INV', '001623'),
		('INV', '001624'),
		('INV', '001625'),
		('INV', '001626'),
		('INV', '001627'),
		('INV', '001628'),
		('INV', '001629'),
		('INV', '001630'),
		('INV', '001631'),
		('INV', '001632'),
		('INV', '001633'),
		('INV', '001634'),
		('INV', '001635'),
		('INV', '001636'),
		('INV', '001637'),
		('INV', '001638'),
		('INV', '001639'),
		('INV', '001640'),
		('INV', '001641'),
		('INV', '001642'),
		('INV', '001643'),
		('INV', '001644'),
		('INV', '001645'),
		('INV', '001646'),
		('INV', '001647'),
		('INV', '001648'),
		('INV', '001649'),
		('INV', '001650'),
		('INV', '001651'),
		('INV', '001652'),
		('INV', '001653'),
		('INV', '001654'),
		('INV', '001655'),
		('INV', '001656'),
		('INV', '001657'),
		('INV', '001658'),
		('INV', '001659'),
		('INV', '001660'),
		('INV', '001661'),
		('INV', '001662'),
		('INV', '001663'),
		('INV', '001664'),
		('INV', '001665'),
		('INV', '001666'),
		('INV', '001667'),
		('INV', '001668'),
		('INV', '001669'),
		('INV', '001670'),
		('INV', '001671'),
		('INV', '001672'),
		('INV', '001673'),
		('INV', '001674'),
		('INV', '001675'),
		('INV', '001676'),
		('INV', '001677'),
		('INV', '001678'),
		('INV', '001679'),
		('INV', '001680'),
		('INV', '001681'),
		('INV', '001682'),
		('INV', '001683'),
		('INV', '001684'),
		('INV', '001685'),
		('INV', '001686'),
		('INV', '001687'),
		('INV', '001688'),
		('INV', '001689'),
		('INV', '001690'),
		('INV', '001691'),
		('INV', '001692'),
		('INV', '001693'),
		('INV', '001694'),
		('INV', '001695'),
		('INV', '001696'),
		('INV', '001697'),
		('INV', '001698'),
		('INV', '001699'),
		('INV', '001700'),
		('INV', '001701'),
		('INV', '001702'),
		('INV', '001703'),
		('INV', '001704'),
		('INV', '001705'),
		('INV', '001706'),
		('INV', '001707'),
		('INV', '001708'),
		('INV', '001709'),
		('INV', '001710'),
		('INV', '001711'),
		('INV', '001712'),
		('INV', '001713'),
		('INV', '001714'),
		('INV', '001715'),
		('INV', '001716'),
		('INV', '001717'),
		('INV', '001718'),
		('INV', '001719'),
		('INV', '001720'),
		('INV', '001721'),
		('INV', '001722'),
		('INV', '001723'),
		('INV', '001724'),
		('INV', '001725'),
		('INV', '001726'),
		('INV', '001727'),
		('INV', '001728'),
		('INV', '001729'),
		('INV', '001730'),
		('INV', '001731'),
		('INV', '001732'),
		('INV', '001733'),
		('INV', '001734'),
		('INV', '001735'),
		('INV', '001736'),
		('INV', '001737'),
		('INV', '001738'),
		('INV', '001739'),
		('INV', '001740'),
		('INV', '001741'),
		('INV', '001742'),
		('INV', '001743'),
		('INV', '001744'),
		('INV', '001745'),
		('INV', '001746'),
		('INV', '001747'),
		('INV', '001748'),
		('INV', '001749'),
		('INV', '001750'),
		('INV', '001751'),
		('INV', '001752'),
		('INV', '001753'),
		('INV', '001754'),
		('INV', '001755'),
		('INV', '001756'),
		('INV', '001757'),
		('INV', '001758'),
		('INV', '001759'),
		('INV', '001760'),
		('INV', '001761'),
		('INV', '001762'),
		('INV', '001763'),
		('INV', '001764'),
		('INV', '001765'),
		('INV', '001766'),
		('INV', '001767'),
		('INV', '001768'),
		('INV', '001769'),
		('INV', '001770'),
		('INV', '001771'),
		('INV', '001772'),
		('INV', '001773'),
		('INV', '001774'),
		('INV', '001775'),
		('INV', '001776'),
		('INV', '001777'),
		('INV', '001778'),
		('INV', '001779'),
		('INV', '001780'),
		('INV', '001781'),
		('INV', '001782'),
		('INV', '001783'),
		('INV', '001784'),
		('INV', '001785'),
		('INV', '001786'),
		('INV', '001787'),
		('INV', '001788'),
		('INV', '001789'),
		('INV', '001790'),
		('INV', '001791'),
		('INV', '001792'),
		('INV', '001793'),
		('INV', '001794'),
		('INV', '001795'),
		('INV', '001796'),
		('INV', '001801'),
		('INV', '001802'),
		('INV', '001803'),
		('INV', '001804'),
		('INV', '001805'),
		('INV', '001806'),
		('INV', '001807'),
		('INV', '001808'),
		('INV', '001809'),
		('INV', '001813'),
		('INV', '001814'),
		('INV', '001815'),
		('INV', '001816'),
		('INV', '001817'),
		('INV', '001818'),
		('INV', '001819'),
		('INV', '001820'),
		('INV', '001821'),
		('INV', '001825'),
		('INV', '001826'),
		('INV', '001827'),
		('INV', '001828'),
		('INV', '001829'),
		('INV', '001830'),
		('INV', '001831'),
		('INV', '001832'),
		('INV', '001833'),
		('INV', '001836'),
		('INV', '001840'),
		('INV', '001841'),
		('INV', '001842'),
		('INV', '001843'),
		('INV', '001844'),
		('INV', '001845'),
		('INV', '001846'),
		('INV', '001847'),
		('INV', '001848'),
		('INV', '001852'),
		('INV', '001853'),
		('INV', '001854'),
		('INV', '001855'),
		('INV', '001856'),
		('INV', '001857'),
		('INV', '001858'),
		('INV', '001859'),
		('INV', '001860'),
		('INV', '001862'),
		('INV', '001863'),
		('INV', '001864'),
		('INV', '001866'),
		('INV', '001867'),
		('INV', '001868'),
		('INV', '001871'),
		('INV', '001872'),
		('INV', '001873'),
		('INV', '001874'),
		('INV', '001875'),
		('INV', '001876'),
		('INV', '001880'),
		('INV', '001881'),
		('INV', '001882'),
		('INV', '001883'),
		('INV', '001884'),
		('INV', '001885'),
		('INV', '001886'),
		('INV', '001887'),
		('INV', '001888'),
		('INV', '001892'),
		('INV', '001893'),
		('INV', '001894'),
		('INV', '001895'),
		('INV', '001896'),
		('INV', '001897'),
		('INV', '001898'),
		('INV', '001899'),
		('INV', '001900'),
		('INV', '001901'),
		('INV', '001902'),
		('INV', '001903'),
		('INV', '001904'),
		('INV', '001905'),
		('INV', '001906'),
		('INV', '001907'),
		('INV', '001908'),
		('INV', '001909'),
		('INV', '001910'),
		('INV', '001911'),
		('INV', '001912'),
		('INV', '001913'),
		('INV', '001914'),
		('INV', '001915'),
		('INV', '001916'),
		('INV', '001917'),
		('INV', '001918'),
		('INV', '001919'),
		('INV', '001920'),
		('INV', '001921'),
		('INV', '001922'),
		('INV', '001923'),
		('INV', '001924'),
		('INV', '001925'),
		('INV', '001926'),
		('INV', '001927'),
		('INV', '001928'),
		('INV', '001929'),
		('INV', '001930'),
		('INV', '001931'),
		('INV', '001932'),
		('INV', '001933'),
		('INV', '001934'),
		('INV', '001935'),
		('INV', '001936'),
		('INV', '001937'),
		('INV', '001938'),
		('INV', '001939'),
		('INV', '001940'),
		('INV', '001941'),
		('INV', '001942'),
		('INV', '001943'),
		('INV', '001944'),
		('INV', '001945'),
		('INV', '001946'),
		('INV', '001947'),
		('INV', '001948'),
		('INV', '001949'),
		('INV', '001950'),
		('INV', '001951'),
		('INV', '001952'),
		('INV', '001953'),
		('INV', '001954'),
		('INV', '001955'),
		('INV', '001956'),
		('INV', '001957'),
		('INV', '001958'),
		('INV', '001959'),
		('INV', '001960'),
		('INV', '001961'),
		('INV', '001962'),
		('INV', '001963'),
		('INV', '001964'),
		('INV', '001965'),
		('INV', '001966'),
		('INV', '001967'),
		('INV', '001968'),
		('INV', '001969'),
		('INV', '001970'),
		('INV', '001971'),
		('INV', '001972'),
		('INV', '001973'),
		('INV', '001974'),
		('INV', '001975'),
		('INV', '001976'),
		('INV', '001977'),
		('INV', '001978'),
		('INV', '001979'),
		('INV', '001980'),
		('INV', '001981'),
		('INV', '001982'),
		('INV', '001983'),
		('INV', '001984'),
		('INV', '001985'),
		('INV', '001986'),
		('INV', '001987'),
		('INV', '001988'),
		('INV', '001989'),
		('INV', '001990'),
		('INV', '001991'),
		('INV', '001992'),
		('INV', '001993'),
		('INV', '001994'),
		('INV', '001995'),
		('INV', '001996'),
		('INV', '001997'),
		('INV', '001998'),
		('INV', '001999'),
		('INV', '002000'),
		('INV', '002002'),
		('INV', '002003'),
		('INV', '002004'),
		('INV', '002005'),
		('INV', '002006'),
		('INV', '002007'),
		('INV', '002008'),
		('INV', '002009'),
		('INV', '002010'),
		('INV', '002011'),
		('INV', '002012'),
		('INV', '002013'),
		('INV', '002014'),
		('INV', '002015'),
		('INV', '002016'),
		('INV', '002017'),
		('INV', '002018'),
		('INV', '002019'),
		('INV', '002020'),
		('INV', '002021'),
		('INV', '002022'),
		('INV', '002023'),
		('INV', '002024'),
		('INV', '002025'),
		('INV', '002026'),
		('INV', '002027'),
		('INV', '002028'),
		('INV', '002029'),
		('INV', '002030'),
		('INV', '002031'),
		('INV', '002032'),
		('INV', '002033'),
		('INV', '002034'),
		('INV', '002035'),
		('INV', '002036'),
		('INV', '002037'),
		('INV', '002038'),
		('INV', '002039'),
		('INV', '002040'),
		('INV', '002041'),
		('INV', '002042'),
		('INV', '002044'),
		('INV', '002045'),
		('INV', '002046'),
		('INV', '002047'),
		('INV', '002048'),
		('INV', '002049'),
		('INV', '002050'),
		('INV', '002051'),
		('INV', '002052'),
		('INV', '002053'),
		('INV', '002054'),
		('INV', '002055'),
		('INV', '002056'),
		('INV', '002057'),
		('INV', '002058'),
		('INV', '002059'),
		('INV', '002060'),
		('INV', '002061'),
		('INV', '002062'),
		('INV', '002063'),
		('INV', '002064'),
		('INV', '002065'),
		('INV', '002066'),
		('INV', '002067'),
		('INV', '002068'),
		('INV', '002069'),
		('INV', '002070'),
		('INV', '002071'),
		('INV', '002072'),
		('INV', '002073'),
		('INV', '002074'),
		('INV', '002075'),
		('INV', '002076'),
		('INV', '002077'),
		('INV', '002078'),
		('INV', '002079'),
		('INV', '002080'),
		('INV', '002081'),
		('INV', '002082'),
		('INV', '002083'),
		('INV', '002084'),
		('INV', '002085'),
		('INV', '002086'),
		('INV', '002087'),
		('INV', '002088'),
		('INV', '002089'),
		('INV', '002090'),
		('INV', '002091'),
		('INV', '002092'),
		('INV', '002093'),
		('INV', '002094'),
		('INV', '002095'),
		('INV', '002096'),
		('INV', '002097'),
		('INV', '002098'),
		('INV', '002099'),
		('INV', '002100'),
		('INV', '002101'),
		('INV', '002102'),
		('INV', '002103'),
		('INV', '002104'),
		('INV', '002105'),
		('INV', '002106'),
		('INV', '002107'),
		('INV', '002108'),
		('INV', '002109'),
		('INV', '002110'),
		('INV', '002111'),
		('INV', '002112'),
		('INV', '002113'),
		('INV', '002114'),
		('INV', '002115'),
		('INV', '002116'),
		('INV', '002117'),
		('INV', '002118'),
		('INV', '002119'),
		('INV', '002120'),
		('INV', '002121'),
		('INV', '002122'),
		('INV', '002123'),
		('INV', '002124'),
		('INV', '002125'),
		('INV', '002126'),
		('INV', '002127'),
		('INV', '002128'),
		('INV', '002129'),
		('INV', '002130'),
		('INV', '002131'),
		('INV', '002132'),
		('INV', '002133'),
		('INV', '002134'),
		('INV', '002135'),
		('INV', '002136'),
		('INV', '002137'),
		('INV', '002138'),
		('INV', '002139'),
		('INV', '002140'),
		('INV', '002141'),
		('INV', '002142'),
		('INV', '002143'),
		('INV', '002144'),
		('INV', '002145'),
		('INV', '002146'),
		('INV', '002147'),
		('INV', '002148'),
		('INV', '002149'),
		('INV', '002150'),
		('INV', '002151'),
		('INV', '002152'),
		('INV', '002153'),
		('INV', '002154'),
		('INV', '002155'),
		('INV', '002156'),
		('INV', '002157'),
		('INV', '002158'),
		('INV', '002159'),
		('INV', '002160'),
		('INV', '002161'),
		('INV', '002162'),
		('INV', '002163'),
		('INV', '002164'),
		('INV', '002165'),
		('INV', '002166'),
		('INV', '002167'),
		('INV', '002168'),
		('INV', '002169'),
		('INV', '002170'),
		('INV', '002171'),
		('INV', '002172'),
		('INV', '002173'),
		('INV', '002174'),
		('INV', '002175'),
		('INV', '002176'),
		('INV', '002177'),
		('INV', '002178'),
		('INV', '002179'),
		('INV', '002180'),
		('INV', '002181'),
		('INV', '002182'),
		('INV', '002183'),
		('INV', '002184'),
		('INV', '002185'),
		('INV', '002186'),
		('INV', '002187'),
		('INV', '002188'),
		('INV', '002189'),
		('INV', '002190'),
		('INV', '002191'),
		('INV', '002192'),
		('INV', '002193'),
		('INV', '002194'),
		('INV', '002195'),
		('INV', '002196'),
		('INV', '002197'),
		('INV', '002198'),
		('INV', '002199'),
		('INV', '002200'),
		('INV', '002201'),
		('INV', '002202'),
		('INV', '002203'),
		('INV', '002204'),
		('INV', '002205'),
		('INV', '002206'),
		('INV', '002207'),
		('INV', '002208'),
		('INV', '002209'),
		('INV', '002210'),
		('INV', '002211'),
		('INV', '002212'),
		('INV', '002213'),
		('INV', '002214'),
		('INV', '002215'),
		('INV', '002216'),
		('INV', '002217'),
		('INV', '002218'),
		('INV', '002219'),
		('INV', '002220'),
		('INV', '002221'),
		('INV', '002222'),
		('INV', '002223'),
		('INV', '002224'),
		('INV', '002225'),
		('INV', '002226'),
		('INV', '002227'),
		('INV', '002228'),
		('INV', '002229'),
		('INV', '002230'),
		('INV', '002231'),
		('INV', '002232'),
		('INV', '002233'),
		('INV', '002234'),
		('INV', '002235'),
		('INV', '002236'),
		('INV', '002237'),
		('INV', '002238'),
		('INV', '002239'),
		('INV', '002240'),
		('INV', '002241'),
		('INV', '002242'),
		('INV', '002243'),
		('INV', '002244'),
		('INV', '002245'),
		('INV', '002246'),
		('INV', '002247'),
		('INV', '002248'),
		('INV', '002249'),
		('INV', '002250'),
		('INV', '002251'),
		('INV', '002252'),
		('INV', '002253'),
		('INV', '002254'),
		('INV', '002255'),
		('INV', '002256'),
		('INV', '002257'),
		('INV', '002258'),
		('INV', '002259'),
		('INV', '002260'),
		('INV', '002261'),
		('INV', '002262'),
		('INV', '002263'),
		('INV', '002264'),
		('INV', '002265'),
		('INV', '002266'),
		('INV', '002267'),
		('INV', '002268'),
		('INV', '002269'),
		('INV', '002270'),
		('INV', '002271'),
		('INV', '002272'),
		('INV', '002273'),
		('INV', '002274'),
		('INV', '002275'),
		('INV', '002276'),
		('INV', '002277'),
		('INV', '002278'),
		('INV', '002279'),
		('INV', '002280'),
		('INV', '002281'),
		('INV', '002282'),
		('INV', '002283'),
		('INV', '002284'),
		('INV', '002285'),
		('INV', '002286'),
		('INV', '002287'),
		('INV', '002288'),
		('INV', '002289'),
		('INV', '002290'),
		('INV', '002291'),
		('INV', '002292'),
		('INV', '002293'),
		('INV', '002294'),
		('INV', '002295'),
		('INV', '002296'),
		('INV', '002297'),
		('INV', '002298'),
		('INV', '002299'),
		('INV', '002300'),
		('INV', '002301'),
		('INV', '002302'),
		('INV', '002303'),
		('INV', '002304'),
		('INV', '002305'),
		('INV', '002306'),
		('INV', '002307'),
		('INV', '002308'),
		('INV', '002309'),
		('INV', '002310'),
		('INV', '002311'),
		('INV', '002312'),
		('INV', '002313'),
		('INV', '002314'),
		('INV', '002315'),
		('INV', '002316'),
		('INV', '002317'),
		('INV', '002318'),
		('INV', '002319'),
		('INV', '002320'),
		('INV', '002321'),
		('INV', '002322'),
		('INV', '002323'),
		('INV', '002324'),
		('INV', '002325'),
		('INV', '002326'),
		('INV', '002327'),
		('INV', '002328'),
		('INV', '002329'),
		('INV', '002330'),
		('INV', '002331'),
		('INV', '002332'),
		('INV', '002333'),
		('INV', '002334'),
		('INV', '002335'),
		('INV', '002336'),
		('INV', '002337'),
		('INV', '002338'),
		('INV', '002339'),
		('INV', '002340'),
		('INV', '002341'),
		('INV', '002342'),
		('INV', '002343'),
		('INV', '002344'),
		('INV', '002345'),
		('INV', '002346'),
		('INV', '002347'),
		('INV', '002348'),
		('INV', '002349'),
		('INV', '002350'),
		('INV', '002351'),
		('INV', '002352'),
		('INV', '002353'),
		('INV', '002354'),
		('INV', '002355'),
		('INV', '002356'),
		('INV', '002357'),
		('INV', '002358'),
		('INV', '002359'),
		('INV', '002360'),
		('INV', '002361'),
		('INV', '002362'),
		('INV', '002363'),
		('INV', '002364'),
		('INV', '002365'),
		('INV', '002366'),
		('INV', '002367'),
		('INV', '002368'),
		('INV', '002369'),
		('INV', '002370'),
		('INV', '002371'),
		('INV', '002372'),
		('INV', '002373'),
		('INV', '002374'),
		('INV', '002375'),
		('INV', '002376'),
		('INV', '002377'),
		('INV', '002378'),
		('INV', '002379'),
		('INV', '002380'),
		('INV', '002381'),
		('INV', '002382'),
		('INV', '002383'),
		('INV', '002384'),
		('INV', '002385'),
		('INV', '002386'),
		('INV', '002387'),
		('INV', '002388'),
		('INV', '002389'),
		('INV', '002390'),
		('INV', '002391'),
		('INV', '002392'),
		('INV', '002393'),
		('INV', '002394'),
		('INV', '002395'),
		('INV', '002396'),
		('INV', '002397'),
		('INV', '002398'),
		('INV', '002399'),
		('INV', '002400'),
		('INV', '002401'),
		('INV', '002402'),
		('INV', '002403'),
		('INV', '002404'),
		('INV', '002405'),
		('INV', '002406'),
		('INV', '002407'),
		('INV', '002408'),
		('INV', '002409'),
		('INV', '002410'),
		('INV', '002411'),
		('INV', '002412'),
		('INV', '002413'),
		('INV', '002414'),
		('INV', '002415'),
		('INV', '002416'),
		('INV', '002417'),
		('INV', '002418'),
		('INV', '002419'),
		('INV', '002420'),
		('INV', '002421'),
		('INV', '002422'),
		('INV', '002423'),
		('INV', '002424'),
		('INV', '002425'),
		('INV', '002426'),
		('INV', '002427'),
		('INV', '002428'),
		('INV', '002429'),
		('INV', '002430'),
		('INV', '002431'),
		('INV', '002432'),
		('INV', '002433'),
		('INV', '002434'),
		('INV', '002435'),
		('INV', '002436'),
		('INV', '002437'),
		('INV', '002438'),
		('INV', '002439'),
		('INV', '002440'),
		('INV', '002441'),
		('INV', '002442'),
		('INV', '002443'),
		('INV', '002444'),
		('INV', '002445'),
		('INV', '002446'),
		('INV', '002447'),
		('INV', '002448'),
		('INV', '002449'),
		('INV', '002450'),
		('INV', '002451'),
		('INV', '002452'),
		('INV', '002453'),
		('INV', '002454'),
		('INV', '002458'),
		('INV', '002459'),
		('INV', '002460'),
		('INV', '002461'),
		('INV', '002462'),
		('INV', '002463'),
		('INV', '002464'),
		('INV', '002465'),
		('INV', '002466'),
		('INV', '002467'),
		('INV', '002468'),
		('INV', '002469'),
		('INV', '002470'),
		('INV', '002471'),
		('INV', '002472'),
		('INV', '002473'),
		('INV', '002474'),
		('INV', '002475'),
		('INV', '002476'),
		('INV', '002477'),
		('INV', '002478'),
		('INV', '002479'),
		('INV', '002480'),
		('INV', '002481'),
		('INV', '002482'),
		('INV', '002483'),
		('INV', '002484'),
		('INV', '002485'),
		('INV', '002486'),
		('INV', '002487'),
		('INV', '002488'),
		('INV', '002489'),
		('INV', '002490'),
		('INV', '002491'),
		('INV', '002492'),
		('INV', '002493'),
		('INV', '002494'),
		('INV', '002495'),
		('INV', '002496'),
		('INV', '002497'),
		('INV', '002498'),
		('INV', '002499'),
		('INV', '002500'),
		('INV', '002501'),
		('INV', '002502'),
		('INV', '002503'),
		('INV', '002504'),
		('INV', '002505'),
		('INV', '002506'),
		('INV', '002507'),
		('INV', '002508'),
		('INV', '002509'),
		('INV', '002510'),
		('INV', '002511'),
		('INV', '002512'),
		('INV', '002513'),
		('INV', '002514'),
		('INV', '002515'),
		('INV', '002516'),
		('INV', '002517'),
		('INV', '002518'),
		('INV', '002519'),
		('INV', '002520'),
		('INV', '002521'),
		('INV', '002522'),
		('INV', '002523'),
		('INV', '002524'),
		('INV', '002525'),
		('INV', '002526'),
		('INV', '002527'),
		('INV', '002528'),
		('INV', '002529'),
		('INV', '002530'),
		('INV', '002531'),
		('INV', '002532'),
		('INV', '002533'),
		('INV', '002534'),
		('INV', '002535'),
		('INV', '002536'),
		('INV', '002537'),
		('INV', '002538'),
		('INV', '002539'),
		('INV', '002540'),
		('INV', '002541'),
		('INV', '002542'),
		('INV', '002543'),
		('INV', '002544'),
		('INV', '002545'),
		('INV', '002546'),
		('INV', '002547'),
		('INV', '002548'),
		('INV', '002549'),
		('INV', '002550'),
		('INV', '002551'),
		('INV', '002552'),
		('INV', '002553'),
		('INV', '002554'),
		('INV', '002555'),
		('INV', '002556'),
		('INV', '002557'),
		('INV', '002558'),
		('INV', '002559'),
		('INV', '002560'),
		('INV', '002561'),
		('INV', '002562'),
		('INV', '002563'),
		('INV', '002564'),
		('INV', '002565'),
		('INV', '002566'),
		('INV', '002567'),
		('INV', '002568'),
		('INV', '002569'),
		('INV', '002570'),
		('INV', '002571'),
		('INV', '002572'),
		('INV', '002573'),
		('INV', '002574'),
		('INV', '002575'),
		('INV', '002576'),
		('INV', '002577'),
		('INV', '002578'),
		('INV', '002579'),
		('INV', '002580'),
		('INV', '002581'),
		('INV', '002582'),
		('INV', '002583'),
		('INV', '002584'),
		('INV', '002585'),
		('INV', '002586'),
		('INV', '002587'),
		('INV', '002588'),
		('INV', '002589'),
		('INV', '002590'),
		('INV', '002591'),
		('INV', '002592'),
		('INV', '002593'),
		('INV', '002594'),
		('INV', '002595'),
		('INV', '002596'),
		('INV', '002597'),
		('INV', '002598'),
		('INV', '002599'),
		('INV', '002600'),
		('INV', '002601'),
		('INV', '002602'),
		('INV', '002603'),
		('INV', '002604'),
		('INV', '002605'),
		('INV', '002606'),
		('INV', '002607'),
		('INV', '002608'),
		('INV', '002713'),
		('INV', '002714'),
		('INV', '002715'),
		('INV', '002716'),
		('INV', '002717'),
		('INV', '002718'),
		('INV', '002719'),
		('INV', '002720'),
		('INV', '002721'),
		('INV', '002722'),
		('INV', '002723'),
		('INV', '002724'),
		('INV', '002725'),
		('INV', '002726'),
		('INV', '002727'),
		('INV', '002728'),
		('INV', '002729'),
		('INV', '002730'),
		('INV', '002731'),
		('INV', '002732'),
		('INV', '002733'),
		('INV', '002734'),
		('INV', '002735'),
		('INV', '002736'),
		('INV', '002737'),
		('INV', '002738'),
		('INV', '002739'),
		('INV', '002740'),
		('INV', '002741'),
		('INV', '002742'),
		('INV', '002743'),
		('INV', '002744'),
		('INV', '002745'),
		('INV', '002746'),
		('INV', '002747'),
		('INV', '002748'),
		('INV', '002749'),
		('INV', '002750'),
		('INV', '002751'),
		('INV', '002752'),
		('INV', '002753'),
		('INV', '002754'),
		('INV', '002755'),
		('INV', '002756'),
		('INV', '002757'),
		('INV', '002758'),
		('INV', '002759'),
		('INV', '002760'),
		('INV', '002761'),
		('INV', '002762'),
		('INV', '002763'),
		('INV', '002764'),
		('INV', '002765'),
		('INV', '002766'),
		('INV', '002767'),
		('INV', '002768'),
		('INV', '002769'),
		('INV', '002770'),
		('INV', '002771'),
		('INV', '002772'),
		('INV', '002773'),
		('INV', '002774'),
		('INV', '002775'),
		('INV', '002776'),
		('INV', '002777'),
		('INV', '002778'),
		('INV', '002779'),
		('INV', '002780'),
		('INV', '002781'),
		('INV', '002782'),
		('INV', '002783'),
		('INV', '002784'),
		('INV', '002785'),
		('INV', '002786'),
		('INV', '002787'),
		('INV', '002788'),
		('INV', '002789'),
		('INV', '002790'),
		('INV', '002791'),
		('INV', '002792'),
		('INV', '002793'),
		('INV', '002794'),
		('INV', '002795'),
		('INV', '002796'),
		('INV', '002797'),
		('INV', '002798'),
		('INV', '002799'),
		('INV', '002800'),
		('INV', '002801'),
		('INV', '002802'),
		('INV', '002803'),
		('INV', '002804'),
		('INV', '002805'),
		('INV', '002806'),
		('INV', '002807'),
		('INV', '002808'),
		('INV', '002809'),
		('INV', '002810'),
		('INV', '002811'),
		('INV', '002812'),
		('INV', '002813'),
		('INV', '002814'),
		('INV', '002815'),
		('INV', '002816'),
		('INV', '002817'),
		('INV', '002818'),
		('INV', '002819'),
		('INV', '002820'),
		('INV', '002821'),
		('INV', '002822'),
		('INV', '002823'),
		('INV', '002824'),
		('INV', '002825'),
		('INV', '002826'),
		('INV', '002827'),
		('INV', '002828'),
		('INV', '002829'),
		('INV', '002830'),
		('INV', '002831'),
		('INV', '002832'),
		('INV', '002833'),
		('INV', '002834'),
		('INV', '002835'),
		('INV', '002836'),
		('INV', '002837'),
		('INV', '002838'),
		('INV', '002839'),
		('INV', '002840'),
		('INV', '002841'),
		('INV', '002842'),
		('INV', '002843'),
		('INV', '002844'),
		('INV', '002845'),
		('INV', '002846'),
		('INV', '002847'),
		('INV', '002848'),
		('INV', '002849'),
		('INV', '002850'),
		('INV', '002851'),
		('INV', '002852'),
		('INV', '002853'),
		('INV', '002854'),
		('INV', '002855'),
		('INV', '002856'),
		('INV', '002857'),
		('INV', '002858'),
		('INV', '002859'),
		('INV', '002860'),
		('INV', '002861'),
		('INV', '002862'),
		('INV', '002863'),
		('INV', '002864'),
		('INV', '002865'),
		('INV', '002866'),
		('INV', '002867'),
		('INV', '002868'),
		('INV', '002869'),
		('INV', '002870'),
		('INV', '002871'),
		('INV', '002872'),
		('INV', '002873'),
		('INV', '002874'),
		('INV', '002875'),
		('INV', '002876'),
		('INV', '002877'),
		('INV', '002878'),
		('INV', '002879'),
		('INV', '002880'),
		('INV', '002881'),
		('INV', '002882'),
		('INV', '002883'),
		('INV', '002884'),
		('INV', '002885'),
		('INV', '002886'),
		('INV', '002887'),
		('INV', '002888'),
		('INV', '002889'),
		('INV', '002890'),
		('INV', '002891'),
		('INV', '002892'),
		('INV', '002893'),
		('INV', '002894'),
		('INV', '002895'),
		('INV', '002896'),
		('INV', '002897'),
		('INV', '002898'),
		('INV', '002899'),
		('INV', '002900'),
		('INV', '002901'),
		('INV', '002902'),
		('INV', '002903'),
		('INV', '002904'),
		('INV', '002905'),
		('INV', '002906'),
		('INV', '002907'),
		('INV', '002908'),
		('INV', '002909'),
		('INV', '002910'),
		('INV', '002911'),
		('INV', '002912'),
		('INV', '002913'),
		('INV', '002914'),
		('INV', '002915'),
		('INV', '002916'),
		('INV', '002917'),
		('INV', '002918'),
		('INV', '002919'),
		('INV', '002920'),
		('INV', '002921'),
		('INV', '002922'),
		('INV', '002923'),
		('INV', '002924'),
		('INV', '002925'),
		('INV', '002926'),
		('INV', '002927'),
		('INV', '002928'),
		('INV', '002929'),
		('INV', '002930'),
		('INV', '002931'),
		('INV', '002932'),
		('INV', '002933'),
		('INV', '002934'),
		('INV', '002935'),
		('INV', '002936'),
		('INV', '002937'),
		('INV', '002938'),
		('INV', '002939'),
		('INV', '002940'),
		('INV', '002941'),
		('INV', '002942'),
		('INV', '002943'),
		('INV', '002944'),
		('INV', '002945'),
		('INV', '002946'),
		('INV', '002947'),
		('INV', '002948'),
		('INV', '002949'),
		('INV', '002950'),
		('INV', '002951'),
		('INV', '002952'),
		('INV', '002953'),
		('INV', '002954'),
		('INV', '002955'),
		('INV', '002956'),
		('INV', '002957'),
		('INV', '002958'),
		('INV', '002959'),
		('INV', '002960'),
		('INV', '002961'),
		('INV', '002962'),
		('INV', '002963'),
		('INV', '002964'),
		('INV', '002965'),
		('INV', '002966'),
		('INV', '002967'),
		('INV', '002968'),
		('INV', '002969'),
		('INV', '002970'),
		('INV', '002971'),
		('INV', '002972'),
		('INV', '002973'),
		('INV', '002974'),
		('INV', '002975'),
		('INV', '002976'),
		('INV', '002977'),
		('INV', '002978'),
		('INV', '002979'),
		('INV', '002980'),
		('INV', '002981'),
		('INV', '002982'),
		('INV', '002983'),
		('INV', '002984'),
		('INV', '002985'),
		('INV', '002986'),
		('INV', '002987'),
		('INV', '002988'),
		('INV', '002989'),
		('INV', '002990'),
		('INV', '002991'),
		('INV', '002992'),
		('INV', '002993'),
		('INV', '002994'),
		('INV', '002995'),
		('INV', '002996'),
		('INV', '002997'),
		('INV', '002998'),
		('INV', '002999'),
		('INV', '003000'),
		('INV', '003001'),
		('INV', '003002'),
		('INV', '003003'),
		('INV', '003004'),
		('INV', '003005'),
		('INV', '003006'),
		('INV', '003007'),
		('INV', '003008'),
		('INV', '003009'),
		('INV', '003010'),
		('INV', '003011'),
		('INV', '003012'),
		('INV', '003013'),
		('INV', '003014'),
		('INV', '003015'),
		('INV', '003016'),
		('INV', '003017'),
		('INV', '003018'),
		('INV', '003019'),
		('INV', '003020'),
		('INV', '003021'),
		('INV', '003022'),
		('INV', '003023'),
		('INV', '003024'),
		('INV', '003025'),
		('INV', '003026'),
		('INV', '003027'),
		('INV', '003028'),
		('INV', '003029'),
		('INV', '003030'),
		('INV', '003031'),
		('INV', '003032'),
		('INV', '003033'),
		('INV', '003034'),
		('INV', '003035'),
		('INV', '003036'),
		('INV', '003037'),
		('INV', '003038'),
		('INV', '003039'),
		('INV', '003040'),
		('INV', '003041'),
		('INV', '003042'),
		('INV', '003043'),
		('INV', '003044'),
		('INV', '003045'),
		('INV', '003046'),
		('INV', '003047'),
		('INV', '003048'),
		('INV', '003049'),
		('INV', '003050'),
		('INV', '003051'),
		('INV', '003052'),
		('INV', '003053'),
		('INV', '003054'),
		('INV', '003055'),
		('INV', '003056'),
		('INV', '003057'),
		('INV', '003058'),
		('INV', '003059'),
		('INV', '003060'),
		('INV', '003061'),
		('INV', '003062'),
		('INV', '003063'),
		('INV', '003064'),
		('INV', '003065'),
		('INV', '003066'),
		('INV', '003067'),
		('INV', '003068'),
		('INV', '003069'),
		('INV', '003070'),
		('INV', '003071'),
		('INV', '003072'),
		('INV', '003073'),
		('INV', '003074'),
		('INV', '003075'),
		('INV', '003076'),
		('INV', '003077'),
		('INV', '003078'),
		('INV', '003079'),
		('INV', '003080'),
		('INV', '003081'),
		('INV', '003082'),
		('INV', '003083'),
		('INV', '003084'),
		('INV', '003085'),
		('INV', '003086'),
		('INV', '003087'),
		('INV', '003088'),
		('INV', '003089'),
		('INV', '003090'),
		('INV', '003091'),
		('INV', '003092'),
		('INV', '003093'),
		('INV', '003094'),
		('INV', '003095'),
		('INV', '003096'),
		('INV', '003097'),
		('INV', '003098'),
		('INV', '003099'),
		('INV', '003100'),
		('INV', '003101'),
		('INV', '003102'),
		('INV', '003103'),
		('INV', '003104'),
		('INV', '003105'),
		('INV', '003106'),
		('INV', '003107'),
		('INV', '003108'),
		('INV', '003109'),
		('INV', '003110'),
		('INV', '003111'),
		('INV', '003112'),
		('INV', '003113'),
		('INV', '003114'),
		('INV', '003115'),
		('INV', '003116'),
		('INV', '003117'),
		('INV', '003118'),
		('INV', '003119'),
		('INV', '003120'),
		('INV', '003121'),
		('INV', '003122'),
		('INV', '003123'),
		('INV', '003124'),
		('INV', '003125'),
		('INV', '003126'),
		('INV', '003127'),
		('INV', '003128'),
		('INV', '003129'),
		('INV', '003130'),
		('INV', '003131'),
		('INV', '003132'),
		('INV', '003133'),
		('INV', '003134'),
		('INV', '003135'),
		('INV', '003136'),
		('INV', '003137'),
		('INV', '003138'),
		('INV', '003139'),
		('INV', '003140'),
		('INV', '003141'),
		('INV', '003142'),
		('INV', '003143'),
		('INV', '003144'),
		('INV', '003145'),
		('INV', '003146'),
		('INV', '003147'),
		('INV', '003148'),
		('INV', '003149'),
		('INV', '003150'),
		('INV', '003151'),
		('INV', '003152'),
		('INV', '003153'),
		('INV', '003154'),
		('INV', '003155'),
		('INV', '003156'),
		('INV', '003157'),
		('INV', '003158'),
		('INV', '003159'),
		('INV', '003160'),
		('INV', '003161'),
		('INV', '003162'),
		('INV', '003163'),
		('INV', '003164'),
		('INV', '003165'),
		('INV', '003166'),
		('INV', '003167'),
		('INV', '003168'),
		('INV', '003169'),
		('INV', '003170'),
		('INV', '003171'),
		('INV', '003172'),
		('INV', '003173'),
		('INV', '003174'),
		('INV', '003175'),
		('INV', '003176'),
		('INV', '003177'),
		('INV', '003178'),
		('INV', '003179'),
		('INV', '003180'),
		('INV', '003181'),
		('INV', '003182'),
		('INV', '003183'),
		('INV', '003184'),
		('INV', '003185'),
		('INV', '003186'),
		('INV', '003187'),
		('INV', '003188'),
		('INV', '003189'),
		('INV', '003190'),
		('INV', '003191'),
		('INV', '003192'),
		('INV', '003193'),
		('INV', '003194'),
		('INV', '003195'),
		('INV', '003196'),
		('INV', '003197'),
		('INV', '003198'),
		('INV', '003199'),
		('INV', '003200'),
		('INV', '003201'),
		('INV', '003202'),
		('INV', '003203'),
		('INV', '003204'),
		('INV', '003205'),
		('INV', '003206'),
		('INV', '003207'),
		('INV', '003208'),
		('INV', '003209'),
		('INV', '003210'),
		('INV', '003211'),
		('INV', '003212'),
		('INV', '003213'),
		('INV', '003214'),
		('INV', '003215'),
		('INV', '003216'),
		('INV', '003217'),
		('INV', '003218'),
		('INV', '003219'),
		('INV', '003220'),
		('INV', '003221'),
		('INV', '003222'),
		('INV', '003223'),
		('INV', '003224'),
		('INV', '003225'),
		('INV', '003226'),
		('INV', '003227'),
		('QCK', '001318'),
		('QCK', '001319'),
		('QCK', '001320'),
		('QCK', '001321'),
		('QCK', '001339'),
		('QCK', '001564'),
		('QCK', '001565'),
		('QCK', '001566'),
		('QCK', '001661'),
		('QCK', '001711'),
		('QCK', '001819'),
		('QCK', '002013'),
		('QCK', '002036')
) KnownKeys(DocType, RefNbr) ON
	KnownKeys.DocType = T.TranType
	AND KnownKeys.RefNbr = T.RefNbr
WHERE T.CompanyID = 2
```