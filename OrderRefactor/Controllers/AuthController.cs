using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.Extensions.Options;
using OrderRefactor.Configuration;
using OrderRefactor.Data;
using OrderRefactor.Models;
using OrderRefactor.Services;

namespace OrderRefactor.Controllers;

[Route("api/auth")]
[ApiController]
public class AuthController : ControllerBase
{
    /// <summary>How long a refresh token stays valid before it must be re-earned by logging in.</summary>
    public static readonly TimeSpan RefreshTokenLifetime = TimeSpan.FromDays(7);

    private readonly JwtOptions _jwtOptions;
    private readonly OrdersDbContext _context;
    private readonly IClock _clock;

    public AuthController(IOptions<JwtOptions> jwtOptions, OrdersDbContext context, IClock clock)
    {
        _jwtOptions = jwtOptions.Value;
        _context = context;
        _clock = clock;
    }

    // Every timestamp below goes through this, never DateTime.UtcNow. Token
    // expiry is the whole point of this controller, so the ability to move time
    // in a test is not a nicety - it is the only way to exercise the expiry
    // branch without either sleeping for seven days or writing a doctored row
    // into the database and skipping the code under test entirely.
    private DateTime Now => _clock.UtcNow.UtcDateTime;

    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] LoginRequest request)
    {
        if (request.Email != "admin@quotes.com" || request.Password != "SecurePassword123")
        {
            return Unauthorized(new { message = "Invalid credentials" });
        }

        var accessToken = GenerateJwtToken(request.Email);
        var refreshTokenValue = GenerateRefreshTokenString();

        var refreshToken = new RefreshToken
        {
            TokenHash = HashToken(refreshTokenValue),
            UserId = request.Email,
            ExpiresAt = Now.Add(RefreshTokenLifetime),
            RevokedAt = null,
            ReplacedByToken = null
        };

        _context.RefreshTokens.Add(refreshToken);
        await _context.SaveChangesAsync();

        return Ok(new
        {
            access_token = accessToken,
            refresh_token = refreshTokenValue,
            expires_in = (int)_jwtOptions.AccessTokenLifetime.TotalSeconds
        });
    }

    [HttpPost("refresh")]
    public async Task<IActionResult> Refresh([FromBody] RefreshRequest request)
    {
        if (string.IsNullOrEmpty(request.RefreshToken))
        {
            return BadRequest(new { message = "Refresh token is required" });
        }

        var incomingHash = HashToken(request.RefreshToken);
        var existingToken = await _context.RefreshTokens
            .FirstOrDefaultAsync(rt => rt.TokenHash == incomingHash);

        // 1. Check if token exists
        if (existingToken == null)
        {
            return Unauthorized(new { message = "Invalid refresh token" });
        }

        // 2. Detect reuse of a leaked token
        if (existingToken.RevokedAt != null)
        {
            // Revoke every live token in the same user's chain to force re-auth.
            var familyTokens = await _context.RefreshTokens
                .Where(rt => rt.UserId == existingToken.UserId && rt.RevokedAt == null)
                .ToListAsync();

            var revokedAt = Now;
            foreach (var token in familyTokens)
            {
                token.RevokedAt = revokedAt;
            }
            await _context.SaveChangesAsync();

            return Unauthorized(new { message = "Security Alert: Refresh token reuse detected. Family revoked." });
        }

        // 3. Check if expired
        if (existingToken.ExpiresAt < Now)
        {
            return Unauthorized(new { message = "Refresh token expired" });
        }

        // 4. Rotate: generate a new pair
        var newAccessToken = GenerateJwtToken(existingToken.UserId);
        var newRefreshTokenValue = GenerateRefreshTokenString();
        var newRefreshHash = HashToken(newRefreshTokenValue);

        // Mark the old token as revoked and replaced
        existingToken.RevokedAt = Now;
        existingToken.ReplacedByToken = newRefreshHash;

        var newRefreshToken = new RefreshToken
        {
            TokenHash = newRefreshHash,
            UserId = existingToken.UserId,
            ExpiresAt = Now.Add(RefreshTokenLifetime)
        };

        _context.RefreshTokens.Add(newRefreshToken);
        await _context.SaveChangesAsync();

        return Ok(new
        {
            access_token = newAccessToken,
            refresh_token = newRefreshTokenValue,
            expires_in = (int)_jwtOptions.AccessTokenLifetime.TotalSeconds
        });
    }

    [HttpPost("logout")]
    public async Task<IActionResult> Logout([FromBody] RefreshRequest request)
    {
        if (string.IsNullOrEmpty(request.RefreshToken))
        {
            return BadRequest(new { message = "Refresh token is required" });
        }

        var incomingHash = HashToken(request.RefreshToken);
        var existingToken = await _context.RefreshTokens
            .FirstOrDefaultAsync(rt => rt.TokenHash == incomingHash);

        if (existingToken != null && existingToken.RevokedAt == null)
        {
            existingToken.RevokedAt = Now;
            await _context.SaveChangesAsync();
        }

        return Ok(new { message = "Logged out successfully" });
    }

    private string GenerateJwtToken(string email)
    {
        var tokenHandler = new JwtSecurityTokenHandler();
        var key = Encoding.UTF8.GetBytes(_jwtOptions.Key);

        var tokenDescriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(new[] { new Claim(ClaimTypes.Name, email) }),
            // IssuedAt/NotBefore are pinned to the same instant so that a test
            // moving the clock produces a coherent token rather than one whose
            // nbf is in the future relative to its own exp.
            IssuedAt = Now,
            NotBefore = Now,
            Expires = Now.Add(_jwtOptions.AccessTokenLifetime),
            Issuer = _jwtOptions.Issuer,
            Audience = _jwtOptions.Audience,
            SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(key), SecurityAlgorithms.HmacSha256Signature)
        };

        var token = tokenHandler.CreateToken(tokenDescriptor);
        return tokenHandler.WriteToken(token);
    }

    private static string GenerateRefreshTokenString()
    {
        var randomNumber = new byte[32];
        using var rng = RandomNumberGenerator.Create();
        rng.GetBytes(randomNumber);
        return Convert.ToBase64String(randomNumber);
    }

    // Refresh tokens are stored hashed, never in plaintext: a database dump
    // must not hand an attacker a set of working tokens.
    private static string HashToken(string token)
    {
        var bytes = Encoding.UTF8.GetBytes(token);
        var hash = SHA256.HashData(bytes);
        return Convert.ToBase64String(hash);
    }
}

public record LoginRequest(string Email, string Password);
public record RefreshRequest(string RefreshToken);
