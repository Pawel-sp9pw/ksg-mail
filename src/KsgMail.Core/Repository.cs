using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace KsgMail.Core;

public sealed class Repository
{
    private readonly string connectionString;

    public Repository(string databasePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(databasePath))!);
        connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath, DefaultTimeout = 15 }.ToString();
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS Customers (Id TEXT PRIMARY KEY, Data TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS Settings (Id INTEGER PRIMARY KEY CHECK(Id=1), Data TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS Dispatch (
                CustomerId TEXT NOT NULL, Period TEXT NOT NULL, Status TEXT NOT NULL,
                PRIMARY KEY(CustomerId, Period));
            CREATE TABLE IF NOT EXISTS DeliveryLog (
                Id INTEGER PRIMARY KEY AUTOINCREMENT, Timestamp TEXT NOT NULL,
                CustomerName TEXT NOT NULL, Email TEXT NOT NULL, Period TEXT NOT NULL,
                Status TEXT NOT NULL, Detail TEXT NOT NULL, MessageId TEXT NOT NULL);
            PRAGMA user_version=1;
            """;
        command.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(connectionString);
        connection.Open();
        return connection;
    }

    public IReadOnlyList<Customer> Customers()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Data FROM Customers";
        using var reader = command.ExecuteReader();
        var customers = new List<Customer>();
        while (reader.Read()) customers.Add(JsonSerializer.Deserialize<Customer>(reader.GetString(0))!);
        return customers.OrderBy(customer => customer.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public void SaveCustomer(Customer customer)
    {
        Validation.Customer(customer);
        Execute("INSERT INTO Customers VALUES($id,$data) ON CONFLICT(Id) DO UPDATE SET Data=$data",
            ("$id", customer.Id), ("$data", JsonSerializer.Serialize(customer)));
    }

    public void DeleteCustomer(string id) => Execute("DELETE FROM Customers WHERE Id=$id", ("$id", id));

    public AppSettings Settings()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Data FROM Settings WHERE Id=1";
        return command.ExecuteScalar() is string data ? JsonSerializer.Deserialize<AppSettings>(data)! : new AppSettings();
    }

    public void SaveSettings(AppSettings settings)
    {
        Validation.Settings(settings);
        Execute("INSERT INTO Settings VALUES(1,$data) ON CONFLICT(Id) DO UPDATE SET Data=$data",
            ("$data", JsonSerializer.Serialize(settings)));
    }

    public bool IsBlocked(string customerId, string period)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Status FROM Dispatch WHERE CustomerId=$id AND Period=$period";
        command.Parameters.AddWithValue("$id", customerId);
        command.Parameters.AddWithValue("$period", period);
        return command.ExecuteScalar() is string status &&
            status is nameof(DeliveryStatus.Success) or nameof(DeliveryStatus.Pending) or nameof(DeliveryStatus.Uncertain);
    }

    public bool Reserve(Customer customer, string period, string messageId, bool allowResend)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = allowResend
            ? """
              INSERT INTO Dispatch VALUES($id,$period,'Pending')
              ON CONFLICT(CustomerId,Period) DO UPDATE SET Status='Pending' WHERE Dispatch.Status != 'Pending'
              """
            : """
              INSERT INTO Dispatch VALUES($id,$period,'Pending')
              ON CONFLICT(CustomerId,Period) DO UPDATE SET Status='Pending' WHERE Dispatch.Status='Failed'
              """;
        command.Parameters.AddWithValue("$id", customer.Id);
        command.Parameters.AddWithValue("$period", period);
        if (command.ExecuteNonQuery() == 0) return false;
        AddLog(connection, transaction, customer, period, DeliveryStatus.Pending, "Rozpoczęto wysyłkę.", messageId);
        transaction.Commit();
        return true;
    }

    public void Finish(Customer customer, string period, DeliveryStatus status, string detail, string messageId)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO Dispatch VALUES($id,$period,$status)
            ON CONFLICT(CustomerId,Period) DO UPDATE SET Status=$status
            """;
        command.Parameters.AddWithValue("$id", customer.Id);
        command.Parameters.AddWithValue("$period", period);
        command.Parameters.AddWithValue("$status", status.ToString());
        command.ExecuteNonQuery();
        using var logCommand = connection.CreateCommand();
        logCommand.Transaction = transaction;
        logCommand.CommandText = "UPDATE DeliveryLog SET Status=$status, Detail=$detail WHERE MessageId=$message AND Status='Pending'";
        logCommand.Parameters.AddWithValue("$status", status.ToString());
        logCommand.Parameters.AddWithValue("$detail", detail);
        logCommand.Parameters.AddWithValue("$message", messageId);
        if (logCommand.ExecuteNonQuery() == 0)
            AddLog(connection, transaction, customer, period, status, detail, messageId);
        transaction.Commit();
    }

    public void Log(Customer customer, string period, DeliveryStatus status, string detail, string messageId = "")
    {
        using var connection = Open();
        AddLog(connection, null, customer, period, status, detail, messageId);
    }

    private static void AddLog(SqliteConnection connection, SqliteTransaction? transaction, Customer customer,
        string period, DeliveryStatus status, string detail, string messageId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO DeliveryLog VALUES(NULL,$time,$name,$email,$period,$status,$detail,$message)";
        command.Parameters.AddWithValue("$time", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$name", customer.Name);
        command.Parameters.AddWithValue("$email", customer.Email);
        command.Parameters.AddWithValue("$period", period);
        command.Parameters.AddWithValue("$status", status.ToString());
        command.Parameters.AddWithValue("$detail", detail);
        command.Parameters.AddWithValue("$message", messageId);
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<DeliveryLog> Logs(int limit = 1000)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM DeliveryLog ORDER BY Id DESC LIMIT $limit";
        command.Parameters.AddWithValue("$limit", limit);
        using var reader = command.ExecuteReader();
        var logs = new List<DeliveryLog>();
        while (reader.Read())
            logs.Add(new DeliveryLog(reader.GetInt64(0), DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture),
                reader.GetString(2), reader.GetString(3), reader.GetString(4),
                Enum.Parse<DeliveryStatus>(reader.GetString(5)), reader.GetString(6), reader.GetString(7)));
        return logs;
    }

    public void RecoverInterrupted()
    {
        Execute("UPDATE Dispatch SET Status='Uncertain' WHERE Status='Pending'");
        Execute("""
            UPDATE DeliveryLog SET Status='Uncertain',
                Detail='Aplikacja przerwała wysyłkę. Sprawdź u odbiorcy przed ponowieniem.'
            WHERE Status='Pending'
            """);
    }

    private void Execute(string sql, params (string Key, object Value)[] parameters)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (key, value) in parameters) command.Parameters.AddWithValue(key, value);
        command.ExecuteNonQuery();
    }
}
