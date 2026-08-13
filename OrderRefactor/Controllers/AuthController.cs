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

namespace OrderRefactor.Controllers;

[Route("api/auth")]
[ApiController]
public class AuthController : ControllerBase
{
    private readonly JwtOptions _jwtOptions;
    private readonly OrdersDbContext _context;

    public AuthController(IOptions<JwtOptions> jwtOptions, OrdersDbContext context)
    {
        _jwtOptions = jwtOptions.Value;
        _context = context;
    }

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
            ExpiresAt = DateTime.UtcNow.AddDays(7),
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

        // 2. Detect Reuse / Leaked Token Attack
        if (existingToken.RevokedAt != null)
        {
            // Revoke all tokens in the same family/user chain to force re-auth
            var familyTokens = await _context.RefreshTokens
                .Where(rt => rt.UserId == existingToken.UserId && rt.RevokedAt == null)
                .ToListAsync();

            foreach (var token in familyTokens)
            {
                token.RevokedAt = DateTime.UtcNow;
            }
            await _context.SaveChangesAsync();

            return Unauthorized(new { message = "Security Alert: Refresh token reuse detected. Family revoked." });
        }

        // 3. Check if expired
        if (existingToken.ExpiresAt < DateTime.UtcNow)
        {
            return Unauthorized(new { message = "Refresh token expired" });
        }

        // 4. Rotate: Generate new pair
        var newAccessToken = GenerateJwtToken(existingToken.UserId);
        var newRefreshTokenValue = GenerateRefreshTokenString();
        var newRefreshHash = HashToken(newRefreshTokenValue);

        // Mark old token as revoked and replaced
        existingToken.RevokedAt = DateTime.UtcNow;
        existingToken.ReplacedByToken = newRefreshHash;

        // Save new token
        var newRefreshToken = new RefreshToken
        {
            TokenHash = newRefreshHash,
            UserId = existingToken.UserId,
            ExpiresAt = DateTime.UtcNow.AddDays(7)
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
            existingToken.RevokedAt = DateTime.UtcNow;
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
            Expires = DateTime.UtcNow.Add(_jwtOptions.AccessTokenLifetime),
            Issuer = _jwtOptions.Issuer,
            Audience = _jwtOptions.Audience,
            SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(key), SecurityAlgorithms.HmacSha256Signature)
        };

        var token = tokenHandler.CreateToken(tokenDescriptor);
        return tokenHandler.WriteToken(token);
    }

    private string GenerateRefreshTokenString()
    {
        var randomNumber = new byte[32];
        using var rng = RandomNumberGenerator.Create();
        rng.GetBytes(randomNumber);
        return Convert.ToBase64String(randomNumber);
    }

    private string HashToken(string token)
    {
        using var sha256 = SHA256.Create();
        var bytes = Encoding.UTF8.GetBytes(token);
        var hash = sha256.ComputeHash(bytes);
        return Convert.ToBase64String(hash);
    }
}

public record LoginRequest(string Email, string Password);
public record RefreshRequest(string RefreshToken);