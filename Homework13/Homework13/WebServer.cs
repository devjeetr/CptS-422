﻿/*
 * Devjeet Roy
 * Student ID: 11404808
 * 
 * */
using System;
using System.Net.Sockets;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading;
using System.Web;
namespace CS422
{
	class WebServer
	{
		private const string STUDENT_ID = "11404808";
		private const string CRLF = "\r\n";
		private const string DOUBLE_CRLF = "\r\n\r\n";

		private const string DEFAULT_TEMPLATE =
			"HTTP/1.1 200 OK\r\n" +
			"Content-Type: text/html\r\n" +
			"\r\n\r\n" +
			"<html>ID Number: {0}<br>" +
			"DateTime.Now: {1}<br>" +
			"Requested URL: {2}</html>";

		// Timeout constants
		private const int DEFAULT_NETWORK_READ_TIMEOUT = 3000;
		private const int DOUBLE_CRLF_TIMEOUT = 10;
		private const int FIRST_CRLF_DATA_TIMEOUT = 2048;
		private const int DOUBLE_CRLF_DATA_TIMEOUT = 100 * 1024;


		// Regex, const and format strings for html
		private const string URL_REGEX_PATTERN = @"(\/.*).*";
		private const string HTTP_VERSION = "HTTP/1.1";
		private const string HEADER_REGEX_PATTERN = @"(.*):(.*)";
		private const string GET_REQUEST_STRING = "GET";


		// Thread stuff
		private static Thread listenerThread;
		private volatile static TcpListener Listener;
		private static List<WebService> services = new List<WebService> ();
		private volatile static int processCount = 0;
		private static volatile bool stopped = false;
		private static SimpleLockThreadPool threadPool;

		public static bool Start(int port, int nThreads = 64)
		{
			
			threadPool = new SimpleLockThreadPool (nThreads);
			Listener = new TcpListener (System.Net.IPAddress.Any, port);
			listenerThread = new Thread(ListenProc);
			Listener.Start ();

			listenerThread.Start();
		
			return true;
		}

		private static void ListenProc(){
			try{

				while(true){
					if(Listener == null){
						Console.WriteLine("NULLLLLLLLLLLLLLLLLLLLLLLLLLLLLLLLLLLLLLLL>>>>>>>>>>>>>>>>");
						break;
					}
					//Console.WriteLine ("Waiting for client!");
					//Console.WriteLine(Listener);
					TcpClient client = Listener.AcceptTcpClient ();
					client.NoDelay = true;
					client.Client.NoDelay = true;

					//Console.WriteLine ("Client accepted!");
					lock(threadPool){

						threadPool.QueueUserWorkItem(ThreadWork, client);
					}

				}
			}
			catch(SocketException e) {
				Console.WriteLine ("SocketException: {0}", e);
			}
		}

		public static void Stop(){
			
			Terminate ();
		}


		private static WebRequest BuildRequest(TcpClient client){
			WebRequest newWebRequest = new WebRequest (client.GetStream());
			ConcatStream bodyStream = null;

			Console.WriteLine("Building Request...");

			List<byte> bufferedRequest = new List<byte>();
			bool done = false;
			int length = -1;

			while(!done){

				NetworkStream networkStream = client.GetStream ();
				lock (networkStream) {
					networkStream.ReadTimeout = DEFAULT_NETWORK_READ_TIMEOUT;
					int available = client.Available;

					var watch = System.Diagnostics.Stopwatch.StartNew();


					while(true)
					{ 
						// wait till client is available
						//while(client.Available != 0){}
						int bufferSize = 1024;
						int bytesRead = 0;
						byte[] buf = new byte[bufferSize];
						try{
							//Console.WriteLine("Client.available: {0}", client.Available);

							if(client.Available > 0){
								bytesRead = networkStream.Read(buf,0,buf.Length);
							}else{
								//Console.WriteLine("client not avail");
								//client.Close ();
								done = true;
								//break;
							}
						}catch(IOException e){
							Console.WriteLine ("Timing out: Read timeout");

							client.Close ();
							//listener.Stop ();
							watch.Stop();
							return null;
						}
						//Console.WriteLine ("Bytes Read: {0}", bytesRead);

						bufferedRequest.AddRange(buf);

						string request = System.Text.ASCIIEncoding.UTF8.GetString (bufferedRequest.ToArray ());

						//Console.WriteLine ();
						//Console.WriteLine ("request:\n{0}", request);
						//Console.WriteLine ();
						// check single crlf timeout
						if (bufferedRequest.Count > FIRST_CRLF_DATA_TIMEOUT
							&& !request.Contains (CRLF)) {
							//Console.WriteLine ("Timing out: First CRLF not found in first {0} bytes", 
								//bufferedRequest.Count);

							//time out!!
							// close socket and return
							client.Close ();
							return null;
						}

						//check timeout #2
						var elapsedSeconds = watch.ElapsedMilliseconds / 1000.0;


						// check for double crlf timeouts
						if (elapsedSeconds >= DOUBLE_CRLF_TIMEOUT
							|| bufferedRequest.Count > DOUBLE_CRLF_DATA_TIMEOUT) {
							if (!request.Contains (DOUBLE_CRLF)) {
								if (elapsedSeconds >= DOUBLE_CRLF_TIMEOUT)
									Console.WriteLine ("Timing out: Double CRLF not found in {0} seconds", 
										elapsedSeconds);
								else
									Console.WriteLine ("Timing out: Double CRLF not found in first {0} bytes", 
										bufferedRequest.Count);

								return null;
							}

						}


						if (bufferedRequest.Count != 0) {
							//Console.WriteLine ("Count != 0: {0}", bufferedRequest.Count );
							if (!isValidRequest (bufferedRequest)) {
								client.Close ();
								//listener.Stop ();
								watch.Stop ();
								return null;
							}

							if (request.Length >= 4 && request.Contains (DOUBLE_CRLF)) {
								//Console.WriteLine ("breaking inner");
								done = true;
								break;
							} 
						} else {
							//Console.WriteLine ("Count = 0");
						}
					}


					var reqString  = System.Text.ASCIIEncoding.UTF8.GetString(bufferedRequest.ToArray());
					if (length == -1 && reqString.Split (new String[] { CRLF}, 
						StringSplitOptions.None).Length > 2) {
						//check for content length
						var headers = parseHeaders(reqString);
						if (headers.ContainsKey ("Content-Length")) {

							length = int.Parse(headers ["Content-Length"]);

						}
					}

					if (done) {
						if (bufferedRequest.Count <= 0)
							return null;
						// find part of body that has been read already
						string reqStr = System.Text.ASCIIEncoding.UTF8.GetString (bufferedRequest.ToArray ());

						int index = reqStr.IndexOf(DOUBLE_CRLF);
						var alreadyReadBody = reqStr.Substring (index + DOUBLE_CRLF.Count());
						//Console.WriteLine ("Already: {0}", alreadyReadBody);
						MemoryStream already = new MemoryStream (System.Text.ASCIIEncoding.UTF8.GetBytes(alreadyReadBody));
						if (length != -1) {
							bodyStream = new ConcatStream (already, networkStream, length);		
						} else {
							bodyStream = new ConcatStream (already, networkStream);	
						}
					}
				}
			}

			// request has been buffered, now build it
			newWebRequest.Method = "GET";
			string requestString = System.Text.ASCIIEncoding.UTF8.GetString(bufferedRequest.ToArray());
			newWebRequest.Body = bodyStream;
			string[] firstLine = requestString.Split (CRLF.ToCharArray()) [0].Split(' ');


			newWebRequest.HTTPVersion = firstLine[2];
			newWebRequest.RequestTarget = 			System.Uri.UnescapeDataString (firstLine [1]);;
			newWebRequest.Headers = parseHeaders (requestString);

			return newWebRequest;

		}

		static ConcurrentDictionary<string, string> parseHeaders(string request){
			String[] requestLines = request.Split(new String[] { CRLF}, 
				StringSplitOptions.None);

			ConcurrentDictionary<string, string> headers = new ConcurrentDictionary<string, string>();
			//Console.WriteLine ("Searching for headers");
			for(int i = 1; i < requestLines.Length; i++){
				if(requestLines[i]  != ""){
					Regex r = new Regex(HEADER_REGEX_PATTERN);

					Match match = r.Match(requestLines[i]);
					string header = match.Groups [1].Value;
					string value = match.Groups [2].Value;
					if (!headers.TryAdd (header, value))
						Console.WriteLine ("Failed to add");

				}	
			}


			return headers;

		}

		static void Terminate(){
			Console.WriteLine ("terminating");
			listenerThread.Abort ();

			if(Listener != null)
				Listener.Stop ();
			Listener = null;
			threadPool.Dispose ();

			Thread.CurrentThread.Abort ();
		}
		
		static void ThreadWork(object clientObj){
			TcpClient client = clientObj as TcpClient;
			while (client.Connected) {
				//Console.WriteLine ("about to build request");
				WebRequest request = BuildRequest (client);

				processCount++;

				//Console.WriteLine ("process: {0}", processCount);
				//Console.WriteLine ("Request built");

				if (request == null) {
					//Console.WriteLine ("Closing connection");

					client.Close ();
				} else {

					var handlerService = fetchHandler (request);

					if (handlerService == null) {
						//Console.WriteLine ("Handler not found, writing not found response");
						request.WriteNotFoundResponse ("not found");
					} else {
						//Console.WriteLine ("Handler found, delegating response");
						handlerService.Handler (request);
					}

				}
				//Console.WriteLine ("Loopoing");
			}

			client.Close ();
			//Console.WriteLine ("Exiting");
		}


		public static void AddService(WebService service){

			services.Add (service);
		}

		private static WebService fetchHandler(WebRequest request){
			
			foreach (var service in services) {
				if (request.RequestTarget != null && request.RequestTarget.StartsWith (service.ServiceURI))
					return service;
			}

			return null;
		}

		private static bool isValidRequest(List<byte> requestBytes){

			String request = System.Text.ASCIIEncoding.UTF8.GetString (requestBytes.ToArray());

			String[] requestLines = request.Split(new String[] { CRLF}, 
												StringSplitOptions.None);

			// DEBUG
			//Console.WriteLine ();
			//Console.WriteLine ("first");
			// process first line for request
			if (requestLines.Length >= 1) {
				Console.WriteLine ("sec");
				if (!processFirstLine (requestLines [0]))
					return false;
			}
			//Console.WriteLine ("3");
			// process headers
			if (requestLines.Length >= 2) {
				//Console.WriteLine ("4");
				for(int i = 1; i < requestLines.Length; i++){
				
					if(requestLines[i]  != ""){
						Regex r = new Regex(HEADER_REGEX_PATTERN);
						//Console.WriteLine ("5");
						if ((!r.IsMatch (requestLines [i])) && 
							(i < requestLines.Length - 1 
								&& requestLines[i+1] == "")) {
							return false;
						}
						//Console.WriteLine ("6");
					}	
				}
			}
			//Console.WriteLine ("7");
			return true;
		}


		private static bool processFirstLine(String line){

			String[] tokens = line.Trim().Split (new char[]{ ' ' }, StringSplitOptions.None);

			if (tokens.Length > 3) {
				return false;
			}
			if (!GET_REQUEST_STRING.Contains (tokens [0]))
				return false;
			if (tokens.Length == 2) {
				Regex r = new Regex (URL_REGEX_PATTERN);

				if (!r.IsMatch (tokens [1]))
					return false;
			}
			if (tokens.Length == 3) {
				if (!HTTP_VERSION.Contains (tokens [2]) && !tokens[2].Contains(HTTP_VERSION)) {
					return false;
				}
				
			}
			return true;
		}
	}
}
	

