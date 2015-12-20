﻿using B2Classes;
using CommandLine;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace B2Uploader
{
    class CmdLineOptions
    {
        [Option('i', "accountid", HelpText = "Account ID", Required=true)]
        public string AccountId { get; set; }

        [Option('a', "appkey", HelpText = "Application Key", Required=true)]
        public string ApplicationKey { get; set; }

        [Option('d', "directory", HelpText = "Directory you want to upload", Required = true)]
        public string Directory { get; set; }

        [Option('m', "multithreads", HelpText = "Number of uploads you want to use at a time. Default is 2", Required = false)]
        public int Threads { get; set; }

        [Option('r', "recursive", HelpText = "Uploads the directory in Recursive mode. Any sub folders will also be uploaded", Required = false)]
        public bool recursive { get; set; }
        
        [Option('v', "verbose", HelpText="Verbose Output")]
        public bool Verbose{get;set;}
    }

    class Program
    {
        static void Main(string[] args)
        {
            var result = CommandLine.Parser.Default.ParseArguments<CmdLineOptions>(args);

            var existCode = result.MapResult(options => {
                if (!Directory.Exists(options.Directory))
                {
                    Console.WriteLine("Directory to upload MUST EXIST!");
                    return 0;
                }

                var auth = AuthorizeUser(options.AccountId, options.ApplicationKey);
                var buckets = ListBuckets(new ListBucketsRequest() { accountId = auth.accountId }, auth.authorizationToken, auth.apiUrl);

                var bucket = buckets.buckets.First();

                SearchOption so = SearchOption.TopDirectoryOnly;
                if (options.recursive)
                {
                    so = SearchOption.AllDirectories;
                }

                string[] FilesToProcess = Directory.GetFiles(options.Directory, "*", so);

                int maxParallel = 2;

                if(options.Threads > 2)
                {
                    maxParallel = options.Threads;
                }

                Parallel.ForEach(FilesToProcess, new ParallelOptions() { MaxDegreeOfParallelism = maxParallel }, s =>
                {
                    //check if file already exists

                    string fileName = getValidFilename(s);

                    var existingFiles = ListFileNames(new ListFileNamesRequest() { bucketId = bucket.bucketId, startFileName = fileName }, auth.apiUrl, auth.authorizationToken);
                    bool found = false;
                    foreach (var x in existingFiles.files)
                    {
                        if (x.fileName == fileName)
                        {
                            //check the file size
                            System.IO.FileInfo fi = new System.IO.FileInfo(s);
                            
                            if (fi.Length == x.size)
                            {
                                found = true;
                                break;
                            }
                            else
                            {
                                //delete old file? could just be an older version... going to upload again...
                                break;
                            }
                        }
                    }
                    if (found)
                    {
                        Console.WriteLine("File {0} exists already, skipping", fileName);
                    }
                    else
                    {
                        bool uploaded = false;
                        int retries = 0;
                        while (!uploaded && retries < 3)
                        {
                            try {
                                var uploadURL = GetUploadURL(new GetUploadURLRequest { bucketId = bucket.bucketId }, auth.apiUrl, auth.authorizationToken);
                                var response = UploadFile(uploadURL.authorizationToken, "b2/x-auto", s, uploadURL.uploadUrl);
                                if(response != null)
                                {
                                    uploaded = true;
                                }
                            }
                            catch(Exception ex)
                            {
                                Console.WriteLine("Uploaded Failed. Retrying");
                                Console.WriteLine(ex.Message);
                                uploaded = false;
                                retries += 1;
                                Thread.Sleep(TimeSpan.FromSeconds(30));
                            }
                        }
                        if (!uploaded)
                        {
                            Console.WriteLine("Uploaded Failed 3 times... Please retry later!");
                        }
                    }
                });
                return 1;
            },
            errors =>{
                Console.WriteLine(errors);
                return 1;
            });
        }

        static AuthorizeResponse AuthorizeUser(string accountId, string applicationKey)
        {
            HttpWebRequest webRequest = (HttpWebRequest)WebRequest.Create("https://api.backblaze.com/b2api/v1/b2_authorize_account");
            string credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes(accountId + ":" + applicationKey));
            webRequest.Headers.Add("Authorization", "Basic " + credentials);
            webRequest.ContentType = "application/json; charset=utf-8";
            WebResponse response = (HttpWebResponse)webRequest.GetResponse();
            var responseString = new StreamReader(response.GetResponseStream()).ReadToEnd();
            response.Close();
            return JsonConvert.DeserializeObject<AuthorizeResponse>(responseString);
        }

        static ListBucketsResponse ListBuckets(ListBucketsRequest request, string authToken, string apiUrl)
        {
            var headers = GetAuthHeaders(authToken);

            string responseString = MakeRequest(apiUrl + "/b2api/v1/b2_list_buckets", headers, JsonConvert.SerializeObject(request));

            return JsonConvert.DeserializeObject<ListBucketsResponse>(responseString);
        }

        static List<Tuple<string,string>> GetAuthHeaders(string authToken)
        {
            List<Tuple<string, string>> headers = new List<Tuple<string, string>>();
            headers.Add(new Tuple<string, string>("Authorization", authToken));
            return headers;
        }

        static GetUploadURLResponse GetUploadURL(GetUploadURLRequest request, string apiUrl, string authToken)
        {

            var headers = GetAuthHeaders(authToken); 
            string responseString = MakeRequest(apiUrl + "/b2api/v1/b2_get_upload_url", headers, JsonConvert.SerializeObject(request));
            
            return JsonConvert.DeserializeObject<GetUploadURLResponse>(responseString);
        }
        static string getValidFilename(string input)
        {
            string fileName = input.Replace('\\', '/');
            if (fileName.StartsWith("/"))
            {
                fileName = fileName.Substring(1);
            }
            return fileName;
        }

        static UploadFileResponse UploadFile(string authToken, string contentType, string filePath, string uploadUrl)
        {
           String sha1 = GetSha1(filePath);

            var headers = GetAuthHeaders(authToken);

            string fileName = getValidFilename(filePath);

            headers.Add(new Tuple<string, string>("X-Bz-File-Name", fileName));
            headers.Add(new Tuple<string, string>("X-Bz-Content-Sha1", sha1));

            using (FileStream fs = System.IO.File.OpenRead(filePath))
            {

                string responseString = MakeRequest(uploadUrl, headers, fs, contentType);

                var resp = JsonConvert.DeserializeObject<UploadFileResponse>(responseString);

                if (resp.contentSha1 == sha1)
                {
                    Console.WriteLine(responseString);
                    return resp;
                }
                else
                {
                    //something went wrong!
                    return null;
                }
            }
        }

        static ListFileNamesResponse ListFileNames(ListFileNamesRequest request, string apiUrl, string authToken)
        {
            var headers = GetAuthHeaders(authToken);
            string responseString =  MakeRequest(apiUrl + "/b2api/v1/b2_list_file_names", headers, JsonConvert.SerializeObject(request));

            return JsonConvert.DeserializeObject<ListFileNamesResponse>(responseString);
        }


        static string MakeRequest(string url, List<Tuple<string,string>> headers, string data, string contentType = "application/json; charset=urf-8")
        {
            MemoryStream ms = new MemoryStream(Encoding.UTF8.GetBytes(data));
            return MakeRequest(url, headers, ms, contentType);
        }
        
        static string MakeRequest(string url, List<Tuple<string,string>> headers, Stream data, string contentType="application/json; charset=utf-8")
        {            
            try
            {
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);

                req.Method = "POST";

                foreach (var head in headers)
                {
                    req.Headers.Add(head.Item1, head.Item2);
                }

                using (var stream = req.GetRequestStream())
                {
                    
                    data.Position = 0;

                    req.ContentType = contentType;
                    
                    data.CopyTo(stream);
                    data.Flush();
                    
                    stream.Close();
                }
                WebResponse response = (HttpWebResponse)req.GetResponse();
                var responseString = new StreamReader(response.GetResponseStream()).ReadToEnd();
                response.Close();

                return responseString;
            }
            catch (Exception ex)
            {
                Console.WriteLine("Error talking to server: {0}", ex.Message);
                Console.WriteLine("URL: {0}", url);
                throw;
            }
        }

        
        
        

        private static string GetSha1(string fileName)
        {
            using (SHA1Managed sha1 = new SHA1Managed())
            {
                using (FileStream fs = System.IO.File.OpenRead(fileName))
                {
                    var hash = sha1.ComputeHash(fs);
                    var sb = new StringBuilder(hash.Length * 2);

                    foreach (byte b in hash)
                    {
                        // can be "x2" if you want lowercase
                        sb.Append(b.ToString("X2"));
                    }
                    return sb.ToString();
                }
            }
        }
    }

    
}
