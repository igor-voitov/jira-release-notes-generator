using Microsoft.AspNetCore.Http;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.Extensions.Logging;
using Microsoft.WindowsAzure.Storage;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using System.Text.RegularExpressions;
using Atlassian;
using Atlassian.Jira;

namespace AzureDevOpsReleaseNotes
{
    public static class ReleaseNotesWebHook
    {
        private static readonly string accountName = Environment.GetEnvironmentVariable("AzureDevOps.accountName");
        private static readonly string projectName = Environment.GetEnvironmentVariable("AzureDevOps.projectName");
        private static readonly string repositoryId = Environment.GetEnvironmentVariable("AzureDevOps.repositoryId");
        private static readonly string pat = Environment.GetEnvironmentVariable("AzureDevOps.pat");
        private static readonly string jiraServer = Environment.GetEnvironmentVariable("Jira.Server");
        private static readonly string jiraUser = Environment.GetEnvironmentVariable("Jira.User");
        private static readonly string jiraPat = Environment.GetEnvironmentVariable("Jira.Pat");

        [FunctionName("ReleaseNotesWebHook")]
        public static async Task<IActionResult> Run([HttpTrigger(AuthorizationLevel.Function, "post", Route = null)]HttpRequest req, ILogger log)
        {
            try
            {
                //Extract function parameters from request body
                string requestBody = new StreamReader(req.Body).ReadToEnd();
                dynamic data = JsonConvert.DeserializeObject(requestBody);
                string fromBuildNumber = data?.fromBuildNumber;
                string currentBuildNumber = data?.currentBuildNumber;
                string buildType = data?.buildType;
                int toBuildId = 0;
                int fromBuildId = 0;

                log.LogInformation("Processing function...");

                //Automatically or manually determine toBuildId and fromBuildId
                if (fromBuildNumber == "Auto")
                {
                    log.LogInformation("fromBuildNumber: Auto");

                    //get toBuildId
                    JObject toBuild = JObject.Parse(GetBuildByNumber(currentBuildNumber));
                    toBuildId = (int)toBuild["value"][0]["id"];
                    log.LogInformation($"toBuildId: {toBuildId}");

                    // get current build's pipeline and sourcebranch 
                    int definitionId = (int)toBuild["value"][0]["definition"]["id"];
                    string sourceBranch = (string)toBuild["value"][0]["sourceBranch"];

                    //get fromBuildId from GetLastSuccededBuilds (lastest ok build)
                    JObject fromBuild = JObject.Parse(GetLastSuccededBuilds(definitionId, sourceBranch));

                    //check if GetLastSuccededBuilds returns any builds, for situation when build is the first build of particular branch
                    if ((int)fromBuild["count"] > 0)
                    {
                        fromBuildId = (int)fromBuild["value"][0]["id"];
                        fromBuildNumber = (string)fromBuild["value"][0]["buildNumber"];
                    }
                    else
                    {
                        fromBuildId = toBuildId;
                    }

                    //additionall check for case when function invoked outside of build process, or if first build of a branch
                    if (fromBuildId == toBuildId)
                    {
                        log.LogInformation("Trying to get previous build because fromBuildId == toBuildId");
                        if ((int)fromBuild["count"] > 1)
                        {
                            log.LogInformation("GetLastSuccededBuilds count > 1");
                            fromBuildId = (int)fromBuild["value"][1]["id"];
                            fromBuildNumber = (string)fromBuild["value"][1]["buildNumber"];
                        }
                        else
                        {
                            log.LogInformation("GetLastSuccededBuilds count <= 1");
                        }
                    }

                    log.LogInformation($"fromBuildId: {fromBuildId}");
                }
                else
                {
                    log.LogInformation("fromBuildNumber: Explicitly set");

                    //get toBuildId
                    JObject toBuild = JObject.Parse(GetBuildByNumber(currentBuildNumber));
                    toBuildId = (int)toBuild["value"][0]["id"];
                    log.LogInformation($"toBuildId: {toBuildId}");

                    //get fromBuildId
                    JObject fromBuild = JObject.Parse(GetBuildByNumber(fromBuildNumber));
                    fromBuildId = (int)fromBuild["value"][0]["id"];
                    log.LogInformation($"fromBuildId: {fromBuildId}");
                }

                // process release notes 
                if (fromBuildId != toBuildId)
                {
                    log.LogInformation("Processing Release Notes...");

                    //Get ChangesBetweenBuilds
                    JObject changesBetweenBuildsMetadata = JObject.Parse(GetChangesBetweenBuilds(fromBuildId, toBuildId));
                    JArray changesBetweenBuilds = (JArray)changesBetweenBuildsMetadata["value"];

                    //Get Jira connection
                    var jira = Jira.CreateRestClient(jiraServer, jiraUser, jiraPat);

                    //Release notes list and header
                    List<string> releaseNotes = new List<string>();
                    releaseNotes.Add($"Version {currentBuildNumber} Release Notes (compared with {fromBuildNumber})\n");
                    releaseNotes.Add($"Included in this release are the following items:\n");

                    //Process each change(commit)
                    foreach (var change in changesBetweenBuilds)
                    {
                        string commitId = (string)change["id"];

                        JObject commit = JObject.Parse(GetCommit(commitId));
                        string commitComment = (string)commit["comment"];
                        string commitUrl = (string)commit["url"];
                        log.LogInformation($"Processing commit: {commitUrl}");

                        string pattern = @"KEY-\d+";
                        Regex rgx = new Regex(pattern);
                        var matches = rgx.Matches(commitComment)
                            .Cast<Match>()
                            .Select(match => match.Value)
                            .Distinct()
                            .ToList();

                        foreach (string jiraKey in matches)
                        {
                            log.LogInformation($"Found issue: {jiraKey}");
                            try
                            {
                                var issue = await jira.Issues.GetIssueAsync(jiraKey);
                                string releaseNote = $"{jiraKey} ({issue.Type}) {issue.Summary}";
                                releaseNotes.Add(releaseNote);
                                log.LogInformation($"Succesfully processed JiraKey: {jiraKey}");
                            }
                            catch
                            {
                                log.LogInformation($"Exception during processing JiraKey: {jiraKey}");                                
                            }
                        }
                    }

                    string releaseNotesAsString = string.Join(Environment.NewLine, releaseNotes.ToArray());
                    string releaseNotesFileName = $"{buildType}_{currentBuildNumber}.txt";

                    //Output to blob
                    CloudStorageAccount storageAccount = CloudStorageAccount.Parse(Environment.GetEnvironmentVariable("StorageAccountConnectionString"));
                    var blobClient = storageAccount.CreateCloudBlobClient();
                    var container = blobClient.GetContainerReference("releasenotes");
                    var blob = container.GetBlockBlobReference(releaseNotesFileName);
                    await blob.UploadTextAsync(releaseNotesAsString);

                    //Output to http response
                    return (ActionResult)new OkObjectResult(releaseNotesFileName);

                }

                else
                {
                    //Release notes list and header
                    List<string> releaseNotes = new List<string>();
                    releaseNotes.Add($"Release Notes for {currentBuildNumber} \n");
                    releaseNotes.Add($"No Release Notes for this build\n");

                    string releaseNotesAsString = string.Join(Environment.NewLine, releaseNotes.ToArray());
                    string releaseNotesFileName = $"{buildType}_{currentBuildNumber}.txt";

                    //Output to blob
                    CloudStorageAccount storageAccount = CloudStorageAccount.Parse(Environment.GetEnvironmentVariable("StorageAccountConnectionString"));
                    var blobClient = storageAccount.CreateCloudBlobClient();
                    var container = blobClient.GetContainerReference("releasenotes");
                    var blob = container.GetBlockBlobReference(releaseNotesFileName);
                    await blob.UploadTextAsync(releaseNotesAsString);

                    //Output to http response
                    return (ActionResult)new OkObjectResult(releaseNotesFileName);

                }

            }
            catch (Exception ex)
            {
                string errorMessage = ex.ToString();
                log.LogInformation(errorMessage);

                return new BadRequestObjectResult(errorMessage);
            }
        } 



        //api calls  

        private static string GetChangesBetweenBuilds(int fromBuildId, int toBuildId)
        {
            string Url = string.Format(
                $@"https://dev.azure.com/{accountName}/{projectName}/_apis/build/changes?fromBuildId={fromBuildId}&toBuildId={toBuildId}&$top=1000&api-version=5.1-preview.2");

            using (HttpClient client = new HttpClient())
            {
                client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(
                        ASCIIEncoding.ASCII.GetBytes(
                        string.Format("{0}:{1}", "", pat))));

                var method = new HttpMethod("GET");
                var request = new HttpRequestMessage(method, Url);

                using (HttpResponseMessage response = client.SendAsync(request).Result)
                {
                    response.EnsureSuccessStatusCode();
                    return response.Content.ReadAsStringAsync().Result;
                }
            }
        }

        private static string GetBuildByNumber(string buildNumber)
        {
            string Url = string.Format(
                $@"https://dev.azure.com/{accountName}/{projectName}/_apis/build/builds?buildNumber={buildNumber}&api-version=5.1-preview.5");

            using (HttpClient client = new HttpClient())
            {
                client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(
                        ASCIIEncoding.ASCII.GetBytes(
                        string.Format("{0}:{1}", "", pat))));

                var method = new HttpMethod("GET");
                var request = new HttpRequestMessage(method, Url);

                using (HttpResponseMessage response = client.SendAsync(request).Result)
                {
                    response.EnsureSuccessStatusCode();
                    return response.Content.ReadAsStringAsync().Result;
                }
            }
        }

        private static string GetLastSuccededBuilds(int definitionId, string sourceBranch)
        {
            string Url = string.Format(
                $@"https://dev.azure.com/{accountName}/{projectName}/_apis/build/builds?definitions={definitionId}&resultFilter=succeeded&$top=2&branchName={sourceBranch}&api-version=5.1-preview.5");

            using (HttpClient client = new HttpClient())
            {
                client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(
                        ASCIIEncoding.ASCII.GetBytes(
                        string.Format("{0}:{1}", "", pat))));

                var method = new HttpMethod("GET");
                var request = new HttpRequestMessage(method, Url);

                using (HttpResponseMessage response = client.SendAsync(request).Result)
                {
                    response.EnsureSuccessStatusCode();
                    return response.Content.ReadAsStringAsync().Result;
                }
            }
        }

        private static string GetCommit(string commitId)
        {
            string Url = string.Format(
                $@"https://dev.azure.com/{accountName}/_apis/git/repositories/{repositoryId}/commits/{commitId}");
            using (HttpClient client = new HttpClient())
            {
                client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(
                        ASCIIEncoding.ASCII.GetBytes(
                        string.Format("{0}:{1}", "", pat))));

                var method = new HttpMethod("GET");
                var request = new HttpRequestMessage(method, Url);

                using (HttpResponseMessage response = client.SendAsync(request).Result)
                {
                    response.EnsureSuccessStatusCode();
                    return response.Content.ReadAsStringAsync().Result;
                }
            }
        }
    }
}
