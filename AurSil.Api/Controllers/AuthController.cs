using System.Text.RegularExpressions;
using AurSil.Api.Models;
using AurSil.Api.Services;
using Dapper;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;

namespace AurSil.Api.Controllers;

[ApiController]
[Route("api/auth")]
public partial class AuthController(IConfiguration config, OtpService otp, EmailSender email, TokenService tokens) : ControllerBase
{
    private readonly string _connectionString = config.GetConnectionString("Aursil")!;

    [GeneratedRegex(@"^[A-Za-z0-9_]{4,50}$")]
    private static partial Regex UserIdRegex();

    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$")]
    private static partial Regex EmailRegex();

    [GeneratedRegex(@"^[6-9]\d{9}$")]
    private static partial Regex PhoneRegex();

    [HttpPost("register")]
    public async Task<IActionResult> Register(RegisterRequest req)
    {
        var userId = req.UserId?.Trim() ?? "";
        var mail = req.Email?.Trim().ToLowerInvariant() ?? "";
        var phone = req.Phone?.Trim() ?? "";
        var name = req.Name?.Trim() ?? "";

        if (!UserIdRegex().IsMatch(userId))
            return BadRequest(new ApiMessage("User ID must be 4-50 letters, numbers or underscore."));
        if (name.Length < 2)
            return BadRequest(new ApiMessage("Please enter your full name."));
        if (!EmailRegex().IsMatch(mail))
            return BadRequest(new ApiMessage("Please enter a valid email address."));
        if (!PhoneRegex().IsMatch(phone))
            return BadRequest(new ApiMessage("Please enter a valid 10-digit mobile number."));
        if (PasswordError(req.Password) is { } pwError)
            return BadRequest(new ApiMessage(pwError));

        await using var db = new SqlConnection(_connectionString);
        var existing = await db.QuerySingleOrDefaultAsync<UserRow>(
            "SELECT * FROM Users WHERE UserId = @userId OR Email = @mail OR Phone = @phone",
            new { userId, mail, phone });

        if (existing is not null)
        {
            // Let an unverified user re-register with the same details to get a fresh OTP
            var sameUnverified = existing.Status == UserStatus.Pending &&
                                 existing.UserId.Equals(userId, StringComparison.OrdinalIgnoreCase) &&
                                 existing.Email == mail;
            if (!sameUnverified)
            {
                var field = existing.UserId.Equals(userId, StringComparison.OrdinalIgnoreCase) ? "User ID"
                          : existing.Email == mail ? "Email" : "Mobile number";
                return Conflict(new ApiMessage($"{field} is already registered."));
            }

            await db.ExecuteAsync(
                "UPDATE Users SET Name = @name, Phone = @phone, PasswordHash = @hash WHERE UserId = @userId",
                new { name, phone, hash = PasswordHasher.Hash(req.Password), userId = existing.UserId });
        }
        else
        {
            await db.ExecuteAsync(
                """
                INSERT INTO Users (UserId, Name, Email, Phone, PasswordHash, Status)
                VALUES (@userId, @name, @mail, @phone, @hash, @status)
                """,
                new { userId, name, mail, phone, hash = PasswordHasher.Hash(req.Password), status = UserStatus.Pending });
        }

       var code = await otp.CreateAsync(mail, OtpPurpose.Register);

await email.SendOtpAsync(
    mail,
    name,
    code,
    OtpPurpose.Register,
    otp.ExpiryMinutes
);

return Ok(new ApiMessage("Registration successful. Please verify your email using the OTP sent."));
    }

    [HttpPost("verify-otp")]
    public async Task<IActionResult> VerifyOtp(VerifyOtpRequest req)
    {
        var mail = req.Email?.Trim().ToLowerInvariant() ?? "";
        var result = await otp.VerifyAsync(mail, OtpPurpose.Register, req.Code ?? "");
        if (result != OtpResult.Valid)
            return BadRequest(new ApiMessage(OtpError(result)));

        await using var db = new SqlConnection(_connectionString);
        await db.ExecuteAsync(
            "UPDATE Users SET Status = @active WHERE Email = @mail AND Status = @pending",
            new { mail, active = UserStatus.Active, pending = UserStatus.Pending });

        return Ok(new ApiMessage("Account verified. You can now log in."));
    }

    [HttpPost("resend-otp")]
    public async Task<IActionResult> ResendOtp(EmailRequest req)
    {
        var mail = req.Email?.Trim().ToLowerInvariant() ?? "";
        await using var db = new SqlConnection(_connectionString);
        var user = await db.QuerySingleOrDefaultAsync<UserRow>(
            "SELECT * FROM Users WHERE Email = @mail AND Status = @pending",
            new { mail, pending = UserStatus.Pending });

        if (user is not null)
        {
            var code = await otp.CreateAsync(mail, OtpPurpose.Register);
            await email.SendOtpAsync(mail, user.Name, code, OtpPurpose.Register, otp.ExpiryMinutes);
        }
        return Ok(new ApiMessage("If the account is awaiting verification, a new OTP has been sent."));
    }

    [HttpPost("login")]
    public async Task<IActionResult> Login(LoginRequest req)
    {
        var login = req.Login?.Trim() ?? "";
        await using var db = new SqlConnection(_connectionString);
        var user = await db.QuerySingleOrDefaultAsync<UserRow>(
            "SELECT * FROM Users WHERE UserId = @login OR Email = @mail",
            new { login, mail = login.ToLowerInvariant() });

        if (user is null || !PasswordHasher.Verify(req.Password ?? "", user.PasswordHash))
            return Unauthorized(new ApiMessage("Invalid User ID / Email or password."));

        if (user.Status == UserStatus.Blocked)
            return StatusCode(403, new ApiMessage("Your account is blocked. Please contact support."));

        if (user.Status == UserStatus.Pending)
        {
            var code = await otp.CreateAsync(user.Email, OtpPurpose.Register);
            await email.SendOtpAsync(user.Email, user.Name, code, OtpPurpose.Register, otp.ExpiryMinutes);
            return StatusCode(403, new { message = "Please verify your email. A new OTP has been sent.", email = user.Email, needsVerification = true });
        }

        await db.ExecuteAsync("UPDATE Users SET LastLoginAt = SYSUTCDATETIME() WHERE UserId = @UserId", new { user.UserId });
        return Ok(new LoginResponse(tokens.Create(user), user.ToProfile()));
    }

    [HttpPost("forgot-password")]
    public async Task<IActionResult> ForgotPassword(EmailRequest req)
    {
        var mail = req.Email?.Trim().ToLowerInvariant() ?? "";
        await using var db = new SqlConnection(_connectionString);
        var user = await db.QuerySingleOrDefaultAsync<UserRow>("SELECT * FROM Users WHERE Email = @mail", new { mail });

        if (user is not null && user.Status != UserStatus.Blocked)
        {
            var code = await otp.CreateAsync(mail, OtpPurpose.ResetPassword);
            await email.SendOtpAsync(mail, user.Name, code, OtpPurpose.ResetPassword, otp.ExpiryMinutes);
        }
        // Same answer either way, so nobody can probe which emails are registered
        return Ok(new ApiMessage("If this email is registered, an OTP has been sent."));
    }

    [HttpPost("reset-password")]
    public async Task<IActionResult> ResetPassword(ResetPasswordRequest req)
    {
        var mail = req.Email?.Trim().ToLowerInvariant() ?? "";
        if (PasswordError(req.NewPassword) is { } pwError)
            return BadRequest(new ApiMessage(pwError));

        var result = await otp.VerifyAsync(mail, OtpPurpose.ResetPassword, req.Code ?? "");
        if (result != OtpResult.Valid)
            return BadRequest(new ApiMessage(OtpError(result)));

        await using var db = new SqlConnection(_connectionString);
        // A reset via emailed OTP also proves the email, so activate pending accounts
        await db.ExecuteAsync(
            "UPDATE Users SET PasswordHash = @hash, Status = CASE WHEN Status = @pending THEN @active ELSE Status END WHERE Email = @mail",
            new { mail, hash = PasswordHasher.Hash(req.NewPassword), pending = UserStatus.Pending, active = UserStatus.Active });

        return Ok(new ApiMessage("Password reset successful. Please log in."));
    }

    private static string? PasswordError(string? password)
    {
        if (string.IsNullOrEmpty(password) || password.Length < 8)
            return "Password must be at least 8 characters.";
        if (!password.Any(char.IsLetter) || !password.Any(char.IsDigit))
            return "Password must contain letters and numbers.";
        return null;
    }

    private static string OtpError(OtpResult result) => result switch
    {
        OtpResult.Expired => "OTP has expired. Please request a new one.",
        OtpResult.TooManyAttempts => "Too many wrong attempts. Please request a new OTP.",
        _ => "Invalid OTP.",
    };
}
