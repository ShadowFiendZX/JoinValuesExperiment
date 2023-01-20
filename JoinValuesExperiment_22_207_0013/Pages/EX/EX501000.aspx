<%@ Page Language="C#" MasterPageFile="~/MasterPages/FormDetail.master" AutoEventWireup="True" ValidateRequest="False" CodeFile="EX501000.aspx.cs" Inherits="Page_EX501000" Title="Untitled Page" %>
<%@ MasterType VirtualPath="~/MasterPages/FormDetail.master" %>

<asp:Content ID="cont1" ContentPlaceHolderID="phDS" Runat="Server">
	<px:PXDataSource ID="ds" runat="server" Visible="True" Width="100%" TypeName="JoinValuesExperiment.EXTestProc" PrimaryView="Filter">
		<CallbackCommands>
		</CallbackCommands>
	</px:PXDataSource>
</asp:Content>
<asp:Content ID="cont2" ContentPlaceHolderID="phF" Runat="Server">
	<px:PXFormView ID="form" runat="server" DataSourceID="ds" Style="z-index: 100" Width="100%" DataMember="Filter" SyncPosition="True">
		<Template>
			<px:PXLayoutRule runat="server" StartRow="True"/>
			<px:PXDropDown ID="edProcessingMethod" runat="server" DataField="ProcessingMethod" CommitChanges="True"/>
		</Template>
	</px:PXFormView>
</asp:Content>
<asp:Content ID="cont3" ContentPlaceHolderID="phG" Runat="Server">
	<px:PXGrid ID="grid" runat="server" Height="400px" Width="100%" Style="z-index: 100" AllowPaging="True" AllowSearch="True" AdjustPageSize="Auto" DataSourceID="ds" SkinID="Inquire" SyncPosition="True">
		<Levels>
			<px:PXGridLevel DataMember="Records">
				<RowTemplate>
					<px:PXCheckBox ID="edSelected" runat="server" DataField="Selected" CommitChanges="True"/>
					<px:PXDropDown ID="edDocType" runat="server" DataField="DocType"/>
					<px:PXSelector ID="edRefNbr" runat="server" DataField="RefNbr"/>
				</RowTemplate>
				<Columns>
					<px:PXGridColumn DataField="Selected" Type="CheckBox" AllowCheckAll="True" CommitChanges="True"/>
					<px:PXGridColumn DataField="DocType"/>
					<px:PXGridColumn DataField="RefNbr"/>
				</Columns>
			</px:PXGridLevel>
		</Levels>
		<AutoSize Container="Window" Enabled="True" MinHeight="200" />
	</px:PXGrid>
</asp:Content>