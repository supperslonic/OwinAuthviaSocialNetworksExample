﻿using AuthDomain;
using AuthDomain.Models.Account;
using AuthWebApi.HttpActionResult;
using AuthWebApi.Models;
using AuthWebApi.Models.Account;
using AuthWebApi.Providers;
using AuthWebApi.Providers.ClaimsMappingStrategies;
using AuthWebApi.Providers.OAuthProviders;
using AuthWebApi.Resources;
using AuthWebApi.Utils;
using Microsoft.AspNet.Identity;
using Microsoft.Owin.Security;
using Microsoft.Owin.Security.Cookies;
using Microsoft.Owin.Security.OAuth;
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Security.Claims;
using System.Threading.Tasks;
using System.Web.Http;

namespace AuthWebApi.Controllers
{
    [RoutePrefix("api/account")]
    public class AccountController : ApiControllerBase
    {
        public UserProvider UserProvider { get; set; }

        public AccountController()
        {
            this.UserProvider = new UserProvider();
        }

        // GET api/account/userInfo
        [HostAuthentication(DefaultAuthenticationTypes.ExternalBearer)]
        [Route("userInfo")]
        public UserRegInfoModel GetUserInfo()
        {
            ClaimsIdentity userIdentity = User.Identity as ClaimsIdentity;
            ExternalLoginModel externalLogin = ExternalLoginModel.FromIdentity(userIdentity);

            return new UserRegInfoModel
            {
                Email = userIdentity.FindFirstValue(ClaimTypes.Email),
                FullName = userIdentity.FindFirstValue(ClaimTypes.GivenName),
                AvatarUrl = userIdentity.FindFirstValue(UserProvider.ClaimTypeAvatarUrl),
                IsRegistered = (externalLogin == null || externalLogin.IsRegistered),
                LoginProvider = (externalLogin != null ? externalLogin.Provider.ToString() : null)
            };
        }

        #region External Login

        // GET api/Account/ExternalLogin
        [AllowAnonymous]
        [HostAuthentication(DefaultAuthenticationTypes.ExternalCookie)] //authenticated by external provider
        [HostAuthentication(DefaultAuthenticationTypes.ExternalBearer)] //refresh token support
        [Route("externalLogin", Name = "externalLogin")]
        public async Task<IHttpActionResult> GetExternalLogin(string provider, string error = null)
        {
            if (error != null)
            {
                return Redirect(Url.Content("~/") + "#error=" + Uri.EscapeDataString(error));
            }

            ExternalLoginProvider loginProvider;
            if (!Enum.TryParse<ExternalLoginProvider>(provider, ignoreCase: true, result: out loginProvider) ||
                loginProvider == ExternalLoginProvider.None)
            {
                //Unsupported login provider
                return InternalServerError();
            }

            if (!User.Identity.IsAuthenticated)
            {
                return new ChallengeResult(loginProvider, this);
            }

            ExternalLoginModel externalLogin = ExternalLoginModel.FromIdentity(User.Identity as ClaimsIdentity);

            if (externalLogin == null)
            {
                return InternalServerError();
            }

            if (externalLogin.Provider != loginProvider)
            {
                Request.GetOwinContext().Authentication.SignOut(
                    DefaultAuthenticationTypes.ExternalCookie,
                    OAuthDefaults.AuthenticationType,
                    CookieAuthenticationDefaults.AuthenticationType);
                return new ChallengeResult(loginProvider, this);
            }

            User user = await this.UserProvider.FindAsync(externalLogin.Provider, externalLogin.ProviderKey);
            if (user != null)
            {
                Request.GetOwinContext().Authentication.SignOut(DefaultAuthenticationTypes.ExternalCookie);
                ClaimsMapper claimsMapper = new RegisteredExternal(user, externalLogin);
                ClaimsIdentity oAuthIdentity = this.UserProvider.CreateIdentity(claimsMapper, OAuthDefaults.AuthenticationType);
                ClaimsIdentity cookieIdentity = this.UserProvider.CreateIdentity(claimsMapper, CookieAuthenticationDefaults.AuthenticationType);
                AuthenticationProperties properties = ApplicationOAuthProvider.CreateProperties(user);
                Request.GetOwinContext().Authentication.SignIn(properties, oAuthIdentity, cookieIdentity);
            }
            else
            {
                ClaimsMapper claimsMapper = new NotRegisteredExternal(externalLogin);
                ClaimsIdentity identity = this.UserProvider.CreateIdentity(claimsMapper, OAuthDefaults.AuthenticationType);
                Request.GetOwinContext().Authentication.SignIn(identity);
            }

            return Ok();
        }

        // GET api/account/externalLogins?returnUrl=%2F&generateState=true
        [AllowAnonymous]
        [Route("externalLogins")]
        public IList<ExternalLoginProviderModel> GetExternalLogins(string returnUrl, bool generateState = false)
        {
            IEnumerable<AuthenticationDescription> descriptions = Request.GetOwinContext().Authentication.GetExternalAuthenticationTypes();
            IList<ExternalLoginProviderModel> providers = new List<ExternalLoginProviderModel>();

            string state;

            if (generateState)
            {
                const int strengthInBits = 256;
                state = RandomOAuthStateGenerator.Generate(strengthInBits);
            }
            else
            {
                state = null;
            }

            foreach (AuthenticationDescription description in descriptions)
            {
                var loginProvider = (ExternalLoginProvider)Enum.Parse(typeof(ExternalLoginProvider),
                    description.Caption,
                    ignoreCase: true);

                ExternalLoginProviderModel model = new ExternalLoginProviderModel
                {
                    Provider = loginProvider,
                    Url = Url.Route("externalLogin", new
                    {
                        provider = description.AuthenticationType,
                        response_type = "token",
                        client_id = Startup.PublicClientId,
                        redirect_uri = new Uri(Request.RequestUri, returnUrl).AbsoluteUri,
                        state = state
                    }),
                    State = state
                };

                providers.Add(model);
            }

            return providers;
        }

        // POST api/account/registerExternal
        [OverrideAuthentication]
        [HostAuthentication(DefaultAuthenticationTypes.ExternalBearer)]
        [Route("registerExternal")]
        public async Task<UserResult> RegisterExternal()
        {
            ExternalLoginModel externalLogin = ExternalLoginModel.FromIdentity(User.Identity as ClaimsIdentity);

            if (externalLogin == null)
            {
                throw new ApiException(Exceptions.ExternalLoginNotFound);
            }

            User user = await this.UserProvider.CreateExternalAsync(externalLogin);
            return new UserResult(user);
        }

        #endregion
	}
}