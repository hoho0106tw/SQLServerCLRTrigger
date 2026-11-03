using System;
using System.Data;
using System.Data.SqlClient;
using System.Globalization;
using Microsoft.SqlServer.Server;

// SQL Server CLR Trigger example.
// Dependencies from the original project: GET_SQL, FUN, MQTrigger.esi.Service1.
// Register this method as a SQL CLR trigger on the relevant source table.
public partial class Triggers
{
    [SqlTrigger(Name = "MQSYSCATCH", Target = "YourSourceTable", Event = "FOR INSERT, UPDATE")]
    public static void MQSYSCATCH()
    {
        using (SqlConnection conn = new SqlConnection("context connection=true"))
        {
            conn.Open();

            // INSERTED is a set, not necessarily a single row.
            DataTable changes = new DataTable();
            using (SqlDataAdapter adapter = new SqlDataAdapter(
                "SELECT Pat_ID1, RxFxUniformDoseIncGray, ActualFractions, " +
                "ActualTotalDoseIncGray, Site_Name, " +
                "CONVERT(varchar(10), Last_DtTm, 111) AS TreatmentDate, " +
                "RxTotalDoseIncGray, RxFractions FROM INSERTED", conn))
            {
                adapter.Fill(changes);
            }

            foreach (DataRow row in changes.Rows)
            {
                TreatmentRecord record = new TreatmentRecord(row);
                try
                {
                    SyncRecord(conn, record);
                }
                catch (Exception ex)
                {
                    LogError(record, ex.Message);
                }
            }
        }
    }

    private static void SyncRecord(SqlConnection conn, TreatmentRecord record)
    {
        if (string.IsNullOrWhiteSpace(record.PatientId) ||
            record.Fractions == "0" || record.TotalDeliveredDose == "0")
            return;

        GET_SQL queries = new GET_SQL();
        MQTrigger.esi.Service1 service = CreateService();

        record.PatientNo = QueryFirst(conn, queries.SELECT_Patient(record.PatientId));
        if (string.IsNullOrWhiteSpace(record.PatientNo))
        {
            LogError(record, "病歷號空白");
            return;
        }

        record.DotId = QueryFirst(conn, queries.getClinic_ID(record.PatientId));
        DataSet cases = service.CHECKISNEWCASE(record.PatientNo);
        DataTable existing = cases.Tables[0];

        if (existing.Rows.Count == 0)
        {
            InsertCase(service, record);
            return;
        }

        DataRow matched = null;
        bool hasSystemCase = false;
        foreach (DataRow candidate in existing.Rows)
        {
            if (!string.Equals(Convert.ToString(candidate[1]).Trim(), "SYSTEM", StringComparison.Ordinal))
                continue;

            hasSystemCase = true;
            if (string.Equals(Convert.ToString(candidate[2]).Trim(), record.BodyPart.Trim(), StringComparison.Ordinal))
                matched = candidate;
        }

        if (!hasSystemCase)
        {
            LogError(record, "舊資料無法做系統新增");
            return;
        }

        if (matched == null)
        {
            InsertCase(service, record);
            return;
        }

        record.RecordId = Convert.ToString(matched[0]);
        service.UPDATERNOTERECMAINTBL(record.PlannedTotalDose, record.PlannedFractions, record.RecordId);

        DataTable matchedDoses = service.GETCOMPAREDOSENUM(record.RecordId, record.Fractions).Tables[0];
        if (matchedDoses.Rows.Count == 0)
        {
            InsertDetail(service, record);
            return;
        }

        // Use decimal for radiation doses; reject invalid numeric values rather than silently overwriting.
        decimal oldDose = ParseDose(Convert.ToString(matchedDoses.Rows[0][0]));
        decimal newDose = ParseDose(record.DosePerFraction);
        if (newDose >= oldDose)
        {
            service.UPDATERNOTERECDETAILTBl(
                record.RecordId, record.Fractions, record.DosePerFraction,
                record.TotalDeliveredDose, record.Date);
        }
    }

    private static void InsertCase(MQTrigger.esi.Service1 service, TreatmentRecord record)
    {
        if (record.PlannedFractions == "0" || record.PlannedTotalDose == "0")
            return;

        FUN helper = new FUN();
        bool isUnique = false;
        for (int attempts = 0; attempts < 100 && !isUnique; attempts++)
        {
            string candidate = helper.RandomStrByNum(helper.RandomIntByInterval(16, 25));
            candidate = candidate.Substring(helper.RandomIntByInterval(1, 5), 10);
            if (service.CHECKID(candidate).Tables[0].Rows.Count == 0)
            {
                record.RecordId = candidate;
                isUnique = true;
            }
        }

        if (!isUnique)
            throw new InvalidOperationException("無法產生未使用的 RNID");

        int inserted = service.INSERTINTORNOTERECMAINTBL(
            record.RecordId, record.PatientNo, record.BodyPart,
            record.PlannedTotalDose, record.PlannedFractions, record.DotId);

        if (inserted > 0)
            InsertDetail(service, record);
    }

    private static void InsertDetail(MQTrigger.esi.Service1 service, TreatmentRecord record)
    {
        service.INSERTINTORNOTERECDETAILTBL(
            record.RecordId, record.Date, record.Fractions,
            record.DosePerFraction, record.TotalDeliveredDose);
    }

    private static string QueryFirst(SqlConnection conn, string sql)
    {
        using (SqlCommand command = new SqlCommand(sql, conn))
        {
            object value = command.ExecuteScalar();
            return value == null || value == DBNull.Value ? "" : Convert.ToString(value);
        }
    }

    private static decimal ParseDose(string value)
    {
        decimal result;
        if (decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out result) ||
            decimal.TryParse(value, NumberStyles.Number, CultureInfo.CurrentCulture, out result))
            return result;

        throw new FormatException("無法解析劑量數值: " + value);
    }

    private static MQTrigger.esi.Service1 CreateService()
    {
        MQTrigger.esi.Service1 service = new MQTrigger.esi.Service1();
        service.Timeout = 60000;
        return service;
    }

    private static void LogError(TreatmentRecord record, string message)
    {
        // Error reporting must not hide the original processing error.
        try
        {
            MQTrigger.esi.Service1 service = new MQTrigger.esi.Service1();
            service.Timeout = 5000;
            service.INSERTINTOMQERRORTBL(
                record.PatientId, record.DosePerFraction, record.Fractions,
                record.TotalDeliveredDose, record.BodyPart, record.Date,
                record.PatientNo, record.RecordId,
                message.Replace("'", "").Replace("&", ""));
        }
        catch
        {
            // In production, add an independent, reliable fallback log.
        }
    }

    private sealed class TreatmentRecord
    {
        public string PatientId, DosePerFraction, Fractions, TotalDeliveredDose;
        public string BodyPart, Date, PlannedTotalDose, PlannedFractions;
        public string PatientNo = "", RecordId = "", DotId = "";

        public TreatmentRecord(DataRow row)
        {
            PatientId = Value(row, 0);
            DosePerFraction = Value(row, 1);
            Fractions = Value(row, 2);
            TotalDeliveredDose = Value(row, 3);
            BodyPart = Value(row, 4);
            Date = Value(row, 5);
            PlannedTotalDose = Value(row, 6);
            PlannedFractions = Value(row, 7);
        }

        private static string Value(DataRow row, int index)
        {
            return row.IsNull(index) ? "" : Convert.ToString(row[index]).Trim();
        }
    }
}
