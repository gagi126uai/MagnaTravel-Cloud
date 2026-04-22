using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace TravelApi.Infrastructure.Persistence.Migrations.Reservations
{
    /// <inheritdoc />
    public partial class InitReservations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "reservas");

            migrationBuilder.CreateTable(
                name: "ApplicationUser",
                schema: "reservas",
                columns: table => new
                {
                    Id = table.Column<string>(type: "text", nullable: false),
                    FullName = table.Column<string>(type: "text", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    UserName = table.Column<string>(type: "text", nullable: true),
                    NormalizedUserName = table.Column<string>(type: "text", nullable: true),
                    Email = table.Column<string>(type: "text", nullable: true),
                    NormalizedEmail = table.Column<string>(type: "text", nullable: true),
                    EmailConfirmed = table.Column<bool>(type: "boolean", nullable: false),
                    PasswordHash = table.Column<string>(type: "text", nullable: true),
                    SecurityStamp = table.Column<string>(type: "text", nullable: true),
                    ConcurrencyStamp = table.Column<string>(type: "text", nullable: true),
                    PhoneNumber = table.Column<string>(type: "text", nullable: true),
                    PhoneNumberConfirmed = table.Column<bool>(type: "boolean", nullable: false),
                    TwoFactorEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    LockoutEnd = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LockoutEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    AccessFailedCount = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApplicationUser", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Customer",
                schema: "reservas",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PublicId = table.Column<Guid>(type: "uuid", nullable: false),
                    FullName = table.Column<string>(type: "text", nullable: false),
                    Email = table.Column<string>(type: "text", nullable: true),
                    Phone = table.Column<string>(type: "text", nullable: true),
                    DocumentNumber = table.Column<string>(type: "text", nullable: true),
                    Address = table.Column<string>(type: "text", nullable: true),
                    Notes = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    TaxId = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    TaxCondition = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    TaxConditionId = table.Column<int>(type: "integer", nullable: true),
                    CreditLimit = table.Column<decimal>(type: "numeric", nullable: false),
                    CurrentBalance = table.Column<decimal>(type: "numeric", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Customer", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "InboxState",
                schema: "reservas",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MessageId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConsumerId = table.Column<Guid>(type: "uuid", nullable: false),
                    LockId = table.Column<Guid>(type: "uuid", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "bytea", rowVersion: true, nullable: true),
                    Received = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ReceiveCount = table.Column<int>(type: "integer", nullable: false),
                    ExpirationTime = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Consumed = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Delivered = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastSequenceNumber = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InboxState", x => x.Id);
                    table.UniqueConstraint("AK_InboxState_MessageId_ConsumerId", x => new { x.MessageId, x.ConsumerId });
                });

            migrationBuilder.CreateTable(
                name: "OutboxState",
                schema: "reservas",
                columns: table => new
                {
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    LockId = table.Column<Guid>(type: "uuid", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "bytea", rowVersion: true, nullable: true),
                    Created = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Delivered = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastSequenceNumber = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OutboxState", x => x.OutboxId);
                });

            migrationBuilder.CreateTable(
                name: "Supplier",
                schema: "reservas",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PublicId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ContactName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Email = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Phone = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    TaxId = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    TaxCondition = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    Address = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CurrentBalance = table.Column<decimal>(type: "numeric", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Supplier", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RefreshToken",
                schema: "reservas",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TokenHash = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    UserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: false),
                    CreatedByIp = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    UserAgent = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    IsPersistent = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    RevokedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ReplacedByTokenHash = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RefreshToken", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RefreshToken_ApplicationUser_UserId",
                        column: x => x.UserId,
                        principalSchema: "reservas",
                        principalTable: "ApplicationUser",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Lead",
                schema: "reservas",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PublicId = table.Column<Guid>(type: "uuid", nullable: false),
                    FullName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Email = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Phone = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    Status = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Source = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    InterestedIn = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    TravelDates = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Travelers = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    EstimatedBudget = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    Notes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    AssignedToUserId = table.Column<string>(type: "text", nullable: true),
                    AssignedToName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    NextFollowUp = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ConvertedCustomerId = table.Column<int>(type: "integer", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ClosedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Lead", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Lead_Customer_ConvertedCustomerId",
                        column: x => x.ConvertedCustomerId,
                        principalSchema: "reservas",
                        principalTable: "Customer",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "OutboxMessage",
                schema: "reservas",
                columns: table => new
                {
                    SequenceNumber = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    EnqueueTime = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    SentTime = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Headers = table.Column<string>(type: "text", nullable: true),
                    Properties = table.Column<string>(type: "text", nullable: true),
                    InboxMessageId = table.Column<Guid>(type: "uuid", nullable: true),
                    InboxConsumerId = table.Column<Guid>(type: "uuid", nullable: true),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: true),
                    MessageId = table.Column<Guid>(type: "uuid", nullable: false),
                    ContentType = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    MessageType = table.Column<string>(type: "text", nullable: false),
                    Body = table.Column<string>(type: "text", nullable: false),
                    ConversationId = table.Column<Guid>(type: "uuid", nullable: true),
                    CorrelationId = table.Column<Guid>(type: "uuid", nullable: true),
                    InitiatorId = table.Column<Guid>(type: "uuid", nullable: true),
                    RequestId = table.Column<Guid>(type: "uuid", nullable: true),
                    SourceAddress = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    DestinationAddress = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    ResponseAddress = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    FaultAddress = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    ExpirationTime = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OutboxMessage", x => x.SequenceNumber);
                    table.ForeignKey(
                        name: "FK_OutboxMessage_InboxState_InboxMessageId_InboxConsumerId",
                        columns: x => new { x.InboxMessageId, x.InboxConsumerId },
                        principalSchema: "reservas",
                        principalTable: "InboxState",
                        principalColumns: new[] { "MessageId", "ConsumerId" });
                    table.ForeignKey(
                        name: "FK_OutboxMessage_OutboxState_OutboxId",
                        column: x => x.OutboxId,
                        principalSchema: "reservas",
                        principalTable: "OutboxState",
                        principalColumn: "OutboxId");
                });

            migrationBuilder.CreateTable(
                name: "Rate",
                schema: "reservas",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PublicId = table.Column<Guid>(type: "uuid", nullable: false),
                    SupplierId = table.Column<int>(type: "integer", nullable: true),
                    ServiceType = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    ProductName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    PriceUnit = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Airline = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    AirlineCode = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: true),
                    Origin = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Destination = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    CabinClass = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    BaggageIncluded = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    HotelName = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: true),
                    City = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    StarRating = table.Column<int>(type: "integer", nullable: true),
                    RoomType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    RoomCategory = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    RoomFeatures = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    MealPlan = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    HotelPriceType = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    ChildrenPayPercent = table.Column<int>(type: "integer", nullable: false),
                    ChildMaxAge = table.Column<int>(type: "integer", nullable: false),
                    PickupLocation = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    DropoffLocation = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    VehicleType = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    MaxPassengers = table.Column<int>(type: "integer", nullable: true),
                    IsRoundTrip = table.Column<bool>(type: "boolean", nullable: false),
                    IncludesFlight = table.Column<bool>(type: "boolean", nullable: false),
                    IncludesHotel = table.Column<bool>(type: "boolean", nullable: false),
                    IncludesTransfer = table.Column<bool>(type: "boolean", nullable: false),
                    IncludesExcursions = table.Column<bool>(type: "boolean", nullable: false),
                    IncludesInsurance = table.Column<bool>(type: "boolean", nullable: false),
                    DurationDays = table.Column<int>(type: "integer", nullable: true),
                    Itinerary = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    NetCost = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    Tax = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    SalePrice = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    Commission = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    ValidFrom = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ValidTo = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    InternalNotes = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Rate", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Rate_Supplier_SupplierId",
                        column: x => x.SupplierId,
                        principalSchema: "reservas",
                        principalTable: "Supplier",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "LeadActivity",
                schema: "reservas",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PublicId = table.Column<Guid>(type: "uuid", nullable: false),
                    LeadId = table.Column<int>(type: "integer", nullable: false),
                    Type = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LeadActivity", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LeadActivity_Lead_LeadId",
                        column: x => x.LeadId,
                        principalSchema: "reservas",
                        principalTable: "Lead",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Reservaciones",
                schema: "reservas",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PublicId = table.Column<Guid>(type: "uuid", nullable: false),
                    NumeroReserva = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    Status = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    StartDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    EndDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ClosedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    PayerId = table.Column<int>(type: "integer", nullable: true),
                    SourceQuoteId = table.Column<int>(type: "integer", nullable: true),
                    SourceLeadId = table.Column<int>(type: "integer", nullable: true),
                    ResponsibleUserId = table.Column<string>(type: "text", nullable: true),
                    WhatsAppPhoneOverride = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    TotalCost = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    TotalSale = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    Balance = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    TotalPaid = table.Column<decimal>(type: "numeric(18,2)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Reservaciones", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Reservaciones_ApplicationUser_ResponsibleUserId",
                        column: x => x.ResponsibleUserId,
                        principalSchema: "reservas",
                        principalTable: "ApplicationUser",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_Reservaciones_Customer_PayerId",
                        column: x => x.PayerId,
                        principalSchema: "reservas",
                        principalTable: "Customer",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_Reservaciones_Lead_SourceLeadId",
                        column: x => x.SourceLeadId,
                        principalSchema: "reservas",
                        principalTable: "Lead",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "HotelBookings",
                schema: "reservas",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PublicId = table.Column<Guid>(type: "uuid", nullable: false),
                    ReservaId = table.Column<int>(type: "integer", nullable: false),
                    SupplierId = table.Column<int>(type: "integer", nullable: false),
                    RateId = table.Column<int>(type: "integer", nullable: true),
                    HotelName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    StarRating = table.Column<int>(type: "integer", nullable: true),
                    Address = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    City = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Country = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    CheckIn = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CheckOut = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Nights = table.Column<int>(type: "integer", nullable: false),
                    RoomType = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    MealPlan = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Rooms = table.Column<int>(type: "integer", nullable: false),
                    Adults = table.Column<int>(type: "integer", nullable: false),
                    Children = table.Column<int>(type: "integer", nullable: false),
                    ConfirmationNumber = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Status = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    NetCost = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    SalePrice = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    Commission = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    Notes = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HotelBookings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_HotelBookings_Rate_RateId",
                        column: x => x.RateId,
                        principalSchema: "reservas",
                        principalTable: "Rate",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_HotelBookings_Reservaciones_ReservaId",
                        column: x => x.ReservaId,
                        principalSchema: "reservas",
                        principalTable: "Reservaciones",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_HotelBookings_Supplier_SupplierId",
                        column: x => x.SupplierId,
                        principalSchema: "reservas",
                        principalTable: "Supplier",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Invoice",
                schema: "reservas",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PublicId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    TipoComprobante = table.Column<int>(type: "integer", nullable: false),
                    PuntoDeVenta = table.Column<int>(type: "integer", nullable: false),
                    NumeroComprobante = table.Column<long>(type: "bigint", nullable: false),
                    CAE = table.Column<string>(type: "text", nullable: true),
                    VencimientoCAE = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Resultado = table.Column<string>(type: "text", nullable: true),
                    Observaciones = table.Column<string>(type: "text", nullable: true),
                    ImporteTotal = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    ImporteNeto = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    ImporteIva = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    WasForced = table.Column<bool>(type: "boolean", nullable: false),
                    ForceReason = table.Column<string>(type: "text", nullable: true),
                    ForcedByUserId = table.Column<string>(type: "text", nullable: true),
                    ForcedByUserName = table.Column<string>(type: "text", nullable: true),
                    ForcedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    OutstandingBalanceAtIssuance = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    AgencySnapshot = table.Column<string>(type: "text", nullable: true),
                    CustomerSnapshot = table.Column<string>(type: "text", nullable: true),
                    ReservaId = table.Column<int>(type: "integer", nullable: true),
                    OriginalInvoiceId = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Invoice", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Invoice_Invoice_OriginalInvoiceId",
                        column: x => x.OriginalInvoiceId,
                        principalSchema: "reservas",
                        principalTable: "Invoice",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_Invoice_Reservaciones_ReservaId",
                        column: x => x.ReservaId,
                        principalSchema: "reservas",
                        principalTable: "Reservaciones",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "ManualCashMovement",
                schema: "reservas",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PublicId = table.Column<Guid>(type: "uuid", nullable: false),
                    Direction = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    OccurredAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Method = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Category = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Reference = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    CreatedBy = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    IsVoided = table.Column<bool>(type: "boolean", nullable: false),
                    VoidedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RelatedReservaId = table.Column<int>(type: "integer", nullable: true),
                    RelatedSupplierId = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ManualCashMovement", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ManualCashMovement_Reservaciones_RelatedReservaId",
                        column: x => x.RelatedReservaId,
                        principalSchema: "reservas",
                        principalTable: "Reservaciones",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_ManualCashMovement_Supplier_RelatedSupplierId",
                        column: x => x.RelatedSupplierId,
                        principalSchema: "reservas",
                        principalTable: "Supplier",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "PackageBookings",
                schema: "reservas",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PublicId = table.Column<Guid>(type: "uuid", nullable: false),
                    ReservaId = table.Column<int>(type: "integer", nullable: false),
                    SupplierId = table.Column<int>(type: "integer", nullable: false),
                    RateId = table.Column<int>(type: "integer", nullable: true),
                    PackageName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Destination = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    StartDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EndDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Nights = table.Column<int>(type: "integer", nullable: false),
                    IncludesHotel = table.Column<bool>(type: "boolean", nullable: false),
                    IncludesFlight = table.Column<bool>(type: "boolean", nullable: false),
                    IncludesTransfer = table.Column<bool>(type: "boolean", nullable: false),
                    IncludesExcursions = table.Column<bool>(type: "boolean", nullable: false),
                    IncludesMeals = table.Column<bool>(type: "boolean", nullable: false),
                    Adults = table.Column<int>(type: "integer", nullable: false),
                    Children = table.Column<int>(type: "integer", nullable: false),
                    Itinerary = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    ConfirmationNumber = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Status = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    NetCost = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    SalePrice = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    Commission = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    Notes = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PackageBookings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PackageBookings_Rate_RateId",
                        column: x => x.RateId,
                        principalSchema: "reservas",
                        principalTable: "Rate",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_PackageBookings_Reservaciones_ReservaId",
                        column: x => x.ReservaId,
                        principalSchema: "reservas",
                        principalTable: "Reservaciones",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PackageBookings_Supplier_SupplierId",
                        column: x => x.SupplierId,
                        principalSchema: "reservas",
                        principalTable: "Supplier",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Passengers",
                schema: "reservas",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PublicId = table.Column<Guid>(type: "uuid", nullable: false),
                    ReservaId = table.Column<int>(type: "integer", nullable: false),
                    FullName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    DocumentType = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    DocumentNumber = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    BirthDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Nationality = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    Phone = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    Email = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Gender = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: true),
                    Notes = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Passengers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Passengers_Reservaciones_ReservaId",
                        column: x => x.ReservaId,
                        principalSchema: "reservas",
                        principalTable: "Reservaciones",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Quote",
                schema: "reservas",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PublicId = table.Column<Guid>(type: "uuid", nullable: false),
                    QuoteNumber = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    Status = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    CustomerId = table.Column<int>(type: "integer", nullable: true),
                    LeadId = table.Column<int>(type: "integer", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ValidUntil = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    AcceptedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    TravelStartDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    TravelEndDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Destination = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Adults = table.Column<int>(type: "integer", nullable: false),
                    Children = table.Column<int>(type: "integer", nullable: false),
                    TotalCost = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    TotalSale = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    GrossMargin = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    ConvertedReservaId = table.Column<int>(type: "integer", nullable: true),
                    Notes = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Quote", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Quote_Customer_CustomerId",
                        column: x => x.CustomerId,
                        principalSchema: "reservas",
                        principalTable: "Customer",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_Quote_Lead_LeadId",
                        column: x => x.LeadId,
                        principalSchema: "reservas",
                        principalTable: "Lead",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_Quote_Reservaciones_ConvertedReservaId",
                        column: x => x.ConvertedReservaId,
                        principalSchema: "reservas",
                        principalTable: "Reservaciones",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "ReservaAttachments",
                schema: "reservas",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PublicId = table.Column<Guid>(type: "uuid", nullable: false),
                    ReservaId = table.Column<int>(type: "integer", nullable: false),
                    FileName = table.Column<string>(type: "text", nullable: false),
                    StoredFileName = table.Column<string>(type: "text", nullable: false),
                    ContentType = table.Column<string>(type: "text", nullable: true),
                    FileSize = table.Column<long>(type: "bigint", nullable: false),
                    UploadedBy = table.Column<string>(type: "text", nullable: true),
                    UploadedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReservaAttachments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ReservaAttachments_Reservaciones_ReservaId",
                        column: x => x.ReservaId,
                        principalSchema: "reservas",
                        principalTable: "Reservaciones",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ServiciosReserva",
                schema: "reservas",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PublicId = table.Column<Guid>(type: "uuid", nullable: false),
                    ReservaId = table.Column<int>(type: "integer", nullable: true),
                    CustomerId = table.Column<int>(type: "integer", nullable: true),
                    SupplierId = table.Column<int>(type: "integer", nullable: true),
                    RateId = table.Column<int>(type: "integer", nullable: true),
                    ConfirmationNumber = table.Column<string>(type: "text", nullable: true),
                    Status = table.Column<string>(type: "text", nullable: false),
                    ServiceType = table.Column<string>(type: "text", nullable: true),
                    ProductType = table.Column<string>(type: "text", nullable: true),
                    Description = table.Column<string>(type: "text", nullable: true),
                    DepartureDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ReturnDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    NetCost = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    SalePrice = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    Commission = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    Tax = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    SupplierName = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ServiceDetailsJson = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ServiciosReserva", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ServiciosReserva_Customer_CustomerId",
                        column: x => x.CustomerId,
                        principalSchema: "reservas",
                        principalTable: "Customer",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_ServiciosReserva_Rate_RateId",
                        column: x => x.RateId,
                        principalSchema: "reservas",
                        principalTable: "Rate",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_ServiciosReserva_Reservaciones_ReservaId",
                        column: x => x.ReservaId,
                        principalSchema: "reservas",
                        principalTable: "Reservaciones",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_ServiciosReserva_Supplier_SupplierId",
                        column: x => x.SupplierId,
                        principalSchema: "reservas",
                        principalTable: "Supplier",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "TransferBookings",
                schema: "reservas",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PublicId = table.Column<Guid>(type: "uuid", nullable: false),
                    ReservaId = table.Column<int>(type: "integer", nullable: false),
                    SupplierId = table.Column<int>(type: "integer", nullable: false),
                    RateId = table.Column<int>(type: "integer", nullable: true),
                    PickupLocation = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    DropoffLocation = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    PickupDateTime = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    FlightNumber = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    VehicleType = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Passengers = table.Column<int>(type: "integer", nullable: false),
                    IsRoundTrip = table.Column<bool>(type: "boolean", nullable: false),
                    ReturnDateTime = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ConfirmationNumber = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Status = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    NetCost = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    SalePrice = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    Commission = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    Notes = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TransferBookings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TransferBookings_Rate_RateId",
                        column: x => x.RateId,
                        principalSchema: "reservas",
                        principalTable: "Rate",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_TransferBookings_Reservaciones_ReservaId",
                        column: x => x.ReservaId,
                        principalSchema: "reservas",
                        principalTable: "Reservaciones",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_TransferBookings_Supplier_SupplierId",
                        column: x => x.SupplierId,
                        principalSchema: "reservas",
                        principalTable: "Supplier",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WhatsAppDelivery",
                schema: "reservas",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ReservaId = table.Column<int>(type: "integer", nullable: false),
                    CustomerId = table.Column<int>(type: "integer", nullable: true),
                    Phone = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Kind = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Direction = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    MessageText = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    AttachmentName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    BotMessageId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    CreatedBy = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    SentBy = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Error = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    PreparedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    SentAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WhatsAppDelivery", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WhatsAppDelivery_Customer_CustomerId",
                        column: x => x.CustomerId,
                        principalSchema: "reservas",
                        principalTable: "Customer",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_WhatsAppDelivery_Reservaciones_ReservaId",
                        column: x => x.ReservaId,
                        principalSchema: "reservas",
                        principalTable: "Reservaciones",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "InvoiceItem",
                schema: "reservas",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    InvoiceId = table.Column<int>(type: "integer", nullable: false),
                    Description = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Quantity = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    UnitPrice = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    Total = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    AlicuotaIvaId = table.Column<int>(type: "integer", nullable: false),
                    ImporteIva = table.Column<decimal>(type: "numeric(18,2)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InvoiceItem", x => x.Id);
                    table.ForeignKey(
                        name: "FK_InvoiceItem_Invoice_InvoiceId",
                        column: x => x.InvoiceId,
                        principalSchema: "reservas",
                        principalTable: "Invoice",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "InvoiceTribute",
                schema: "reservas",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    InvoiceId = table.Column<int>(type: "integer", nullable: false),
                    TributeId = table.Column<int>(type: "integer", nullable: false),
                    Description = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    BaseImponible = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    Alicuota = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    Importe = table.Column<decimal>(type: "numeric(18,2)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InvoiceTribute", x => x.Id);
                    table.ForeignKey(
                        name: "FK_InvoiceTribute_Invoice_InvoiceId",
                        column: x => x.InvoiceId,
                        principalSchema: "reservas",
                        principalTable: "Invoice",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "QuoteItem",
                schema: "reservas",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PublicId = table.Column<Guid>(type: "uuid", nullable: false),
                    QuoteId = table.Column<int>(type: "integer", nullable: false),
                    ServiceType = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Description = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    SupplierId = table.Column<int>(type: "integer", nullable: true),
                    RateId = table.Column<int>(type: "integer", nullable: true),
                    Quantity = table.Column<int>(type: "integer", nullable: false),
                    UnitCost = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    UnitPrice = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    MarkupPercent = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    Notes = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QuoteItem", x => x.Id);
                    table.ForeignKey(
                        name: "FK_QuoteItem_Quote_QuoteId",
                        column: x => x.QuoteId,
                        principalSchema: "reservas",
                        principalTable: "Quote",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_QuoteItem_Rate_RateId",
                        column: x => x.RateId,
                        principalSchema: "reservas",
                        principalTable: "Rate",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_QuoteItem_Supplier_SupplierId",
                        column: x => x.SupplierId,
                        principalSchema: "reservas",
                        principalTable: "Supplier",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "FlightSegments",
                schema: "reservas",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PublicId = table.Column<Guid>(type: "uuid", nullable: false),
                    ReservaId = table.Column<int>(type: "integer", nullable: false),
                    SupplierId = table.Column<int>(type: "integer", nullable: false),
                    AirlineCode = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    AirlineName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    FlightNumber = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Origin = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    OriginCity = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Destination = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    DestinationCity = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    DepartureTime = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ArrivalTime = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CabinClass = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Baggage = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    TicketNumber = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    FareBase = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    PNR = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    Status = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: false),
                    NetCost = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    SalePrice = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    Commission = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    Tax = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    Notes = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    RateId = table.Column<int>(type: "integer", nullable: true),
                    ServicioReservaId = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FlightSegments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FlightSegments_Rate_RateId",
                        column: x => x.RateId,
                        principalSchema: "reservas",
                        principalTable: "Rate",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_FlightSegments_Reservaciones_ReservaId",
                        column: x => x.ReservaId,
                        principalSchema: "reservas",
                        principalTable: "Reservaciones",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_FlightSegments_ServiciosReserva_ServicioReservaId",
                        column: x => x.ServicioReservaId,
                        principalSchema: "reservas",
                        principalTable: "ServiciosReserva",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_FlightSegments_Supplier_SupplierId",
                        column: x => x.SupplierId,
                        principalSchema: "reservas",
                        principalTable: "Supplier",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Payments",
                schema: "reservas",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PublicId = table.Column<Guid>(type: "uuid", nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    PaidAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Method = table.Column<string>(type: "text", nullable: false),
                    Reference = table.Column<string>(type: "text", nullable: true),
                    Status = table.Column<string>(type: "text", nullable: false),
                    Notes = table.Column<string>(type: "text", nullable: true),
                    EntryType = table.Column<string>(type: "text", nullable: false),
                    AffectsCash = table.Column<bool>(type: "boolean", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    DeletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ReservaId = table.Column<int>(type: "integer", nullable: true),
                    ServicioReservaId = table.Column<int>(type: "integer", nullable: true),
                    RelatedInvoiceId = table.Column<int>(type: "integer", nullable: true),
                    OriginalPaymentId = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Payments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Payments_Invoice_RelatedInvoiceId",
                        column: x => x.RelatedInvoiceId,
                        principalSchema: "reservas",
                        principalTable: "Invoice",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_Payments_Payments_OriginalPaymentId",
                        column: x => x.OriginalPaymentId,
                        principalSchema: "reservas",
                        principalTable: "Payments",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_Payments_Reservaciones_ReservaId",
                        column: x => x.ReservaId,
                        principalSchema: "reservas",
                        principalTable: "Reservaciones",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_Payments_ServiciosReserva_ServicioReservaId",
                        column: x => x.ServicioReservaId,
                        principalSchema: "reservas",
                        principalTable: "ServiciosReserva",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "PaymentReceipts",
                schema: "reservas",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PublicId = table.Column<Guid>(type: "uuid", nullable: false),
                    PaymentId = table.Column<int>(type: "integer", nullable: false),
                    ReservaId = table.Column<int>(type: "integer", nullable: false),
                    ReceiptNumber = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    IssuedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    Status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    VoidedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PaymentReceipts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PaymentReceipts_Payments_PaymentId",
                        column: x => x.PaymentId,
                        principalSchema: "reservas",
                        principalTable: "Payments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PaymentReceipts_Reservaciones_ReservaId",
                        column: x => x.ReservaId,
                        principalSchema: "reservas",
                        principalTable: "Reservaciones",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_FlightSegments_RateId",
                schema: "reservas",
                table: "FlightSegments",
                column: "RateId");

            migrationBuilder.CreateIndex(
                name: "IX_FlightSegments_ReservaId",
                schema: "reservas",
                table: "FlightSegments",
                column: "ReservaId");

            migrationBuilder.CreateIndex(
                name: "IX_FlightSegments_ServicioReservaId",
                schema: "reservas",
                table: "FlightSegments",
                column: "ServicioReservaId");

            migrationBuilder.CreateIndex(
                name: "IX_FlightSegments_SupplierId",
                schema: "reservas",
                table: "FlightSegments",
                column: "SupplierId");

            migrationBuilder.CreateIndex(
                name: "IX_HotelBookings_RateId",
                schema: "reservas",
                table: "HotelBookings",
                column: "RateId");

            migrationBuilder.CreateIndex(
                name: "IX_HotelBookings_ReservaId",
                schema: "reservas",
                table: "HotelBookings",
                column: "ReservaId");

            migrationBuilder.CreateIndex(
                name: "IX_HotelBookings_SupplierId",
                schema: "reservas",
                table: "HotelBookings",
                column: "SupplierId");

            migrationBuilder.CreateIndex(
                name: "IX_InboxState_Delivered",
                schema: "reservas",
                table: "InboxState",
                column: "Delivered");

            migrationBuilder.CreateIndex(
                name: "IX_Invoice_OriginalInvoiceId",
                schema: "reservas",
                table: "Invoice",
                column: "OriginalInvoiceId");

            migrationBuilder.CreateIndex(
                name: "IX_Invoice_ReservaId",
                schema: "reservas",
                table: "Invoice",
                column: "ReservaId");

            migrationBuilder.CreateIndex(
                name: "IX_InvoiceItem_InvoiceId",
                schema: "reservas",
                table: "InvoiceItem",
                column: "InvoiceId");

            migrationBuilder.CreateIndex(
                name: "IX_InvoiceTribute_InvoiceId",
                schema: "reservas",
                table: "InvoiceTribute",
                column: "InvoiceId");

            migrationBuilder.CreateIndex(
                name: "IX_Lead_ConvertedCustomerId",
                schema: "reservas",
                table: "Lead",
                column: "ConvertedCustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_LeadActivity_LeadId",
                schema: "reservas",
                table: "LeadActivity",
                column: "LeadId");

            migrationBuilder.CreateIndex(
                name: "IX_ManualCashMovement_RelatedReservaId",
                schema: "reservas",
                table: "ManualCashMovement",
                column: "RelatedReservaId");

            migrationBuilder.CreateIndex(
                name: "IX_ManualCashMovement_RelatedSupplierId",
                schema: "reservas",
                table: "ManualCashMovement",
                column: "RelatedSupplierId");

            migrationBuilder.CreateIndex(
                name: "IX_OutboxMessage_EnqueueTime",
                schema: "reservas",
                table: "OutboxMessage",
                column: "EnqueueTime");

            migrationBuilder.CreateIndex(
                name: "IX_OutboxMessage_ExpirationTime",
                schema: "reservas",
                table: "OutboxMessage",
                column: "ExpirationTime");

            migrationBuilder.CreateIndex(
                name: "IX_OutboxMessage_InboxMessageId_InboxConsumerId_SequenceNumber",
                schema: "reservas",
                table: "OutboxMessage",
                columns: new[] { "InboxMessageId", "InboxConsumerId", "SequenceNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OutboxMessage_OutboxId_SequenceNumber",
                schema: "reservas",
                table: "OutboxMessage",
                columns: new[] { "OutboxId", "SequenceNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OutboxState_Created",
                schema: "reservas",
                table: "OutboxState",
                column: "Created");

            migrationBuilder.CreateIndex(
                name: "IX_PackageBookings_RateId",
                schema: "reservas",
                table: "PackageBookings",
                column: "RateId");

            migrationBuilder.CreateIndex(
                name: "IX_PackageBookings_ReservaId",
                schema: "reservas",
                table: "PackageBookings",
                column: "ReservaId");

            migrationBuilder.CreateIndex(
                name: "IX_PackageBookings_SupplierId",
                schema: "reservas",
                table: "PackageBookings",
                column: "SupplierId");

            migrationBuilder.CreateIndex(
                name: "IX_Passengers_ReservaId",
                schema: "reservas",
                table: "Passengers",
                column: "ReservaId");

            migrationBuilder.CreateIndex(
                name: "IX_PaymentReceipts_PaymentId",
                schema: "reservas",
                table: "PaymentReceipts",
                column: "PaymentId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PaymentReceipts_ReservaId",
                schema: "reservas",
                table: "PaymentReceipts",
                column: "ReservaId");

            migrationBuilder.CreateIndex(
                name: "IX_Payments_OriginalPaymentId",
                schema: "reservas",
                table: "Payments",
                column: "OriginalPaymentId");

            migrationBuilder.CreateIndex(
                name: "IX_Payments_RelatedInvoiceId",
                schema: "reservas",
                table: "Payments",
                column: "RelatedInvoiceId");

            migrationBuilder.CreateIndex(
                name: "IX_Payments_ReservaId",
                schema: "reservas",
                table: "Payments",
                column: "ReservaId");

            migrationBuilder.CreateIndex(
                name: "IX_Payments_ServicioReservaId",
                schema: "reservas",
                table: "Payments",
                column: "ServicioReservaId");

            migrationBuilder.CreateIndex(
                name: "IX_Quote_ConvertedReservaId",
                schema: "reservas",
                table: "Quote",
                column: "ConvertedReservaId");

            migrationBuilder.CreateIndex(
                name: "IX_Quote_CustomerId",
                schema: "reservas",
                table: "Quote",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_Quote_LeadId",
                schema: "reservas",
                table: "Quote",
                column: "LeadId");

            migrationBuilder.CreateIndex(
                name: "IX_QuoteItem_QuoteId",
                schema: "reservas",
                table: "QuoteItem",
                column: "QuoteId");

            migrationBuilder.CreateIndex(
                name: "IX_QuoteItem_RateId",
                schema: "reservas",
                table: "QuoteItem",
                column: "RateId");

            migrationBuilder.CreateIndex(
                name: "IX_QuoteItem_SupplierId",
                schema: "reservas",
                table: "QuoteItem",
                column: "SupplierId");

            migrationBuilder.CreateIndex(
                name: "IX_Rate_SupplierId",
                schema: "reservas",
                table: "Rate",
                column: "SupplierId");

            migrationBuilder.CreateIndex(
                name: "IX_RefreshToken_UserId",
                schema: "reservas",
                table: "RefreshToken",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_ReservaAttachments_ReservaId",
                schema: "reservas",
                table: "ReservaAttachments",
                column: "ReservaId");

            migrationBuilder.CreateIndex(
                name: "IX_Reservaciones_PayerId",
                schema: "reservas",
                table: "Reservaciones",
                column: "PayerId");

            migrationBuilder.CreateIndex(
                name: "IX_Reservaciones_PublicId",
                schema: "reservas",
                table: "Reservaciones",
                column: "PublicId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Reservaciones_ResponsibleUserId",
                schema: "reservas",
                table: "Reservaciones",
                column: "ResponsibleUserId");

            migrationBuilder.CreateIndex(
                name: "IX_Reservaciones_SourceLeadId",
                schema: "reservas",
                table: "Reservaciones",
                column: "SourceLeadId");

            migrationBuilder.CreateIndex(
                name: "IX_ServiciosReserva_CustomerId",
                schema: "reservas",
                table: "ServiciosReserva",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_ServiciosReserva_RateId",
                schema: "reservas",
                table: "ServiciosReserva",
                column: "RateId");

            migrationBuilder.CreateIndex(
                name: "IX_ServiciosReserva_ReservaId",
                schema: "reservas",
                table: "ServiciosReserva",
                column: "ReservaId");

            migrationBuilder.CreateIndex(
                name: "IX_ServiciosReserva_SupplierId",
                schema: "reservas",
                table: "ServiciosReserva",
                column: "SupplierId");

            migrationBuilder.CreateIndex(
                name: "IX_TransferBookings_RateId",
                schema: "reservas",
                table: "TransferBookings",
                column: "RateId");

            migrationBuilder.CreateIndex(
                name: "IX_TransferBookings_ReservaId",
                schema: "reservas",
                table: "TransferBookings",
                column: "ReservaId");

            migrationBuilder.CreateIndex(
                name: "IX_TransferBookings_SupplierId",
                schema: "reservas",
                table: "TransferBookings",
                column: "SupplierId");

            migrationBuilder.CreateIndex(
                name: "IX_WhatsAppDelivery_CustomerId",
                schema: "reservas",
                table: "WhatsAppDelivery",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_WhatsAppDelivery_ReservaId",
                schema: "reservas",
                table: "WhatsAppDelivery",
                column: "ReservaId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FlightSegments",
                schema: "reservas");

            migrationBuilder.DropTable(
                name: "HotelBookings",
                schema: "reservas");

            migrationBuilder.DropTable(
                name: "InvoiceItem",
                schema: "reservas");

            migrationBuilder.DropTable(
                name: "InvoiceTribute",
                schema: "reservas");

            migrationBuilder.DropTable(
                name: "LeadActivity",
                schema: "reservas");

            migrationBuilder.DropTable(
                name: "ManualCashMovement",
                schema: "reservas");

            migrationBuilder.DropTable(
                name: "OutboxMessage",
                schema: "reservas");

            migrationBuilder.DropTable(
                name: "PackageBookings",
                schema: "reservas");

            migrationBuilder.DropTable(
                name: "Passengers",
                schema: "reservas");

            migrationBuilder.DropTable(
                name: "PaymentReceipts",
                schema: "reservas");

            migrationBuilder.DropTable(
                name: "QuoteItem",
                schema: "reservas");

            migrationBuilder.DropTable(
                name: "RefreshToken",
                schema: "reservas");

            migrationBuilder.DropTable(
                name: "ReservaAttachments",
                schema: "reservas");

            migrationBuilder.DropTable(
                name: "TransferBookings",
                schema: "reservas");

            migrationBuilder.DropTable(
                name: "WhatsAppDelivery",
                schema: "reservas");

            migrationBuilder.DropTable(
                name: "InboxState",
                schema: "reservas");

            migrationBuilder.DropTable(
                name: "OutboxState",
                schema: "reservas");

            migrationBuilder.DropTable(
                name: "Payments",
                schema: "reservas");

            migrationBuilder.DropTable(
                name: "Quote",
                schema: "reservas");

            migrationBuilder.DropTable(
                name: "Invoice",
                schema: "reservas");

            migrationBuilder.DropTable(
                name: "ServiciosReserva",
                schema: "reservas");

            migrationBuilder.DropTable(
                name: "Rate",
                schema: "reservas");

            migrationBuilder.DropTable(
                name: "Reservaciones",
                schema: "reservas");

            migrationBuilder.DropTable(
                name: "Supplier",
                schema: "reservas");

            migrationBuilder.DropTable(
                name: "ApplicationUser",
                schema: "reservas");

            migrationBuilder.DropTable(
                name: "Lead",
                schema: "reservas");

            migrationBuilder.DropTable(
                name: "Customer",
                schema: "reservas");
        }
    }
}
