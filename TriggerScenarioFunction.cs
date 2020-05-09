/* 
*  Copyright (c) Microsoft. All rights reserved. Licensed under the MIT license. 
*  See LICENSE in the source repository root for complete license information. 
*/

using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.IdentityModel.Clients.ActiveDirectory;
using Hl7.Fhir.Rest;
using Hl7.Fhir.Model;
using System.Linq;
using System.Net.Http;
using System.Text;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Headers;
using System.Collections.Generic;

namespace RTW.Proactive.Functions
{
    public static class TriggerScenarioFunction
    {
        [FunctionName("TriggerScenarioFunction")]
        public static async Task<IActionResult> Run([HttpTrigger(AuthorizationLevel.Function, "get", "post", Route = null)] HttpRequest req)
        {
            var authority = Environment.GetEnvironmentVariable("FHIR_Authority", EnvironmentVariableTarget.Process);
            var audience = Environment.GetEnvironmentVariable("FHIR_Audience", EnvironmentVariableTarget.Process);
            var clientId = Environment.GetEnvironmentVariable("FHIR_ClientId");
            var clientSecret = Environment.GetEnvironmentVariable("FHIR_ClientSecret");
            var fhirServerUrl = new Uri(Environment.GetEnvironmentVariable("FHIR_URL"));

            var authContext = new AuthenticationContext(authority);
            var clientCredential = new ClientCredential(clientId, clientSecret);
            var authResult = authContext.AcquireTokenAsync(audience, clientCredential).Result;
            var client = new FhirClient(fhirServerUrl) { PreferredFormat = ResourceFormat.Json, UseFormatParam = true, };
            client.OnBeforeRequest += (object sender, BeforeRequestEventArgs e) =>
            {
                e.RawRequest.Headers["Authorization"] = $"Bearer {authResult.AccessToken}";
            };
            var patients = client.Search<Patient>();

            var patientList = new List<Patient>(); 
            while (patients != null)
            {
                foreach (var e in patients.Entry)
                {
                    patientList.Add((Patient)e.Resource);
                }
                patients = client.Continue(patients, PageDirection.Next);
            }

            foreach (var patient in patientList)
            {
                var rawAddress = patient.Extension.FirstOrDefault(p => p.Url == "address");
                if (rawAddress != null)
                {
                    PostScreenTrigger(rawAddress.Value.ToString());
                }
            }
            return new OkResult();
        }

        static async void PostScreenTrigger(string teamsAddress)
        {
            string triggerUri = System.Environment.GetEnvironmentVariable("Healthbot_Trigger_Uri", EnvironmentVariableTarget.Process);

            using (var client = new HttpClient())
            {
                client.BaseAddress = new Uri(triggerUri);
                //Add an Authorization Bearer token (jwt token)
                var partial_token = GetJwtToken();
                client.DefaultRequestHeaders.Add("Authorization", "Bearer " + partial_token);

                // Add an Accept header for JSON format.
                client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

                //Add body parameter
                //var payload = "{\"address\":" + teamsAddress + ",\"scenario\": \"/scenarios/screen\"}";
                var scenarioId = System.Environment.GetEnvironmentVariable("Healthbot_ScenarioId", EnvironmentVariableTarget.Process);
                var payload = "{\"address\":" + teamsAddress + ",\"scenario\": \"/scenarios/" + scenarioId + "\"}";
                HttpContent content = new StringContent(payload, Encoding.UTF8, "application/json");

                // List data response.
                HttpResponseMessage response = await client.PostAsync(triggerUri, content);

            }
        }

        public static string GetJwtToken()
        {
            var secret = Environment.GetEnvironmentVariable("Healthbot_API_JWT_SECRET", EnvironmentVariableTarget.Process);
            var healthbotName = Environment.GetEnvironmentVariable("Healthbot_Name", EnvironmentVariableTarget.Process);
            var t = DateTime.UtcNow - new DateTime(1970, 1, 1);
            var secondsSinceEpoch = (int)t.TotalSeconds;

            var signingKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret));
            var signingCredentials = new SigningCredentials(signingKey, SecurityAlgorithms.HmacSha256);
            var header = new JwtHeader(signingCredentials);
            var payload = new JwtPayload
            {
               { "tenantName", healthbotName},
               { "iat", secondsSinceEpoch},
            };

            var secToken = new JwtSecurityToken(header, payload);
            var handler = new JwtSecurityTokenHandler();

            var tokenString = handler.WriteToken(secToken);
            return tokenString;
        }
    }
}
