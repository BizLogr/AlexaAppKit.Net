﻿//  Copyright 2015 Stefan Negritoiu (FreeBusy). See LICENSE file for more information.

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.Caching;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Org.BouncyCastle.X509;
using Org.BouncyCastle.Security.Certificates;

namespace AlexaSkillsKit.Authentication
{
    public class SpeechletRequestSignatureVerifier
    {
        private const string CERT_CACHE_KEY_PREFIX = "AlexaAppKit_" + Sdk.SIGNATURE_CERT_URL_REQUEST_HEADER;

        private static CacheItemPolicy _policy = new CacheItemPolicy {
            Priority = CacheItemPriority.Default,
            AbsoluteExpiration = DateTimeOffset.UtcNow.AddHours(24)
        };
        
        /// <summary>
        /// Verifies request signature and manages the caching of the signature certificate
        /// </summary>
        public static bool VerifyRequestSignature(
            byte[] serializedSpeechletRequest, string expectedSignature, string certChainUrl) {

            if (!IsValidCertificateUrl(certChainUrl))
            {
                return false;
            }

            X509Certificate cert = MemoryCache.Default.Get(GetCacheKey(certChainUrl)) as X509Certificate;
            if (cert == null ||
                !CheckRequestSignature(serializedSpeechletRequest, expectedSignature, cert)) {

                // download the cert 
                // if we don't have it in cache or
                // if we have it but it's stale because the current request was signed with a newer cert
                // (signaled by signature check fail with cached cert)
                cert = RetrieveAndVerifyCertificate(certChainUrl);
                if (cert == null) return false;

                MemoryCache.Default.Set(GetCacheKey(certChainUrl), cert, _policy);
            }

            return CheckRequestSignature(serializedSpeechletRequest, expectedSignature, cert);
        }


        /// <summary>
        /// Verifies request signature and manages the caching of the signature certificate
        /// </summary>
        public async static Task<bool> VerifyRequestSignatureAsync(
            byte[] serializedSpeechletRequest, string expectedSignature, string certChainUrl) {

            X509Certificate cert = MemoryCache.Default.Get(GetCacheKey(certChainUrl)) as X509Certificate;
            if (cert == null ||
                !CheckRequestSignature(serializedSpeechletRequest, expectedSignature, cert)) {

                // download the cert 
                // if we don't have it in cache or 
                // if we have it but it's stale because the current request was signed with a newer cert
                // (signaled by signature check fail with cached cert)
                cert = await RetrieveAndVerifyCertificateAsync(certChainUrl);
                if (cert == null) return false;

                MemoryCache.Default.Set(GetCacheKey(certChainUrl), cert, _policy);
            }

            return CheckRequestSignature(serializedSpeechletRequest, expectedSignature, cert);
        }


        /// <summary>
        /// 
        /// </summary>
        public static X509Certificate RetrieveAndVerifyCertificate(string certChainUrl) {
            // making requests to externally-supplied URLs is an open invitation to DoS
            // so restrict host to an Alexa controlled subdomain/path
            if (!IsValidCertificateUrl(certChainUrl)) return null;

            var webClient = new WebClient();
            var content = webClient.DownloadString(certChainUrl);

            var pemReader = new Org.BouncyCastle.OpenSsl.PemReader(new StringReader(content));
            var cert = (X509Certificate)pemReader.ReadObject();
            try {
                cert.CheckValidity();
                if (!CheckCertSubjectNames(cert)) return null;
            }
            catch (CertificateExpiredException) {
                return null;
            }
            catch (CertificateNotYetValidException) {
                return null;
            }

            return cert;
        }


        /// <summary>
        /// 
        /// </summary>
        public async static Task<X509Certificate> RetrieveAndVerifyCertificateAsync(string certChainUrl) {
            // making requests to externally-supplied URLs is an open invitation to DoS
            // so restrict host to an Alexa controlled subdomain/path
            if (!IsValidCertificateUrl(certChainUrl)) return null;

            var httpClient = new HttpClient();
            var httpResponse = await httpClient.GetAsync(certChainUrl);
            var content = await httpResponse.Content.ReadAsStringAsync();
            if (String.IsNullOrEmpty(content)) return null;

            var pemReader = new Org.BouncyCastle.OpenSsl.PemReader(new StringReader(content));
            var cert = (X509Certificate)pemReader.ReadObject();
            try {
                cert.CheckValidity(); 
                if (!CheckCertSubjectNames(cert)) return null;
            }
            catch (CertificateExpiredException) {
                return null;
            }
            catch (CertificateNotYetValidException) {
                return null;
            }

            return cert;
        }


        /// <summary>
        /// 
        /// </summary>
        public static bool CheckRequestSignature(
            byte[] serializedSpeechletRequest, string expectedSignature, Org.BouncyCastle.X509.X509Certificate cert) {

            byte[] expectedSig = null;
            try {
                expectedSig = Convert.FromBase64String(expectedSignature);
            }
            catch (FormatException) {
                return false;
            }

            var publicKey = (Org.BouncyCastle.Crypto.Parameters.RsaKeyParameters)cert.GetPublicKey();
            var signer = Org.BouncyCastle.Security.SignerUtilities.GetSigner(Sdk.SIGNATURE_ALGORITHM);
            signer.Init(false, publicKey);
            signer.BlockUpdate(serializedSpeechletRequest, 0, serializedSpeechletRequest.Length);            

            return signer.VerifySignature(expectedSig);
        }


        /// <summary>
        /// 
        /// </summary>
        private static bool CheckCertSubjectNames(X509Certificate cert) {
            bool found = false;
            ArrayList subjectNamesList = (ArrayList)cert.GetSubjectAlternativeNames();
            for (int i=0; i < subjectNamesList.Count; i++) {
                ArrayList subjectNames = (ArrayList)subjectNamesList[i];
                for (int j = 0; j < subjectNames.Count; j++) {
                    if (subjectNames[j] is String && subjectNames[j].Equals(Sdk.ECHO_API_DOMAIN_NAME)) {
                        found = true;
                        break;
                    }
                }
            }

            return found;
        }

        /// <summary>
        /// Validates that the chain URL matches Amazon's requirements.
        /// </summary>
        /// <param name="certChainUrl">The URL to validate.</param>
        /// <returns>Whether or not the URL is valid.</returns>
        private static bool IsValidCertificateUrl(
            string certChainUrl)
        {
            if (string.IsNullOrEmpty(certChainUrl))
            {
                return false;
            }

            if ((certChainUrl.Length >= 8) && (certChainUrl.Substring(0, 8).ToLower() != "https://"))
            {
                return false;
            }

            if ((certChainUrl.Length >= 24) && (certChainUrl.Substring(0, 24).ToLower() != "https://s3.amazonaws.com"))
            {
                return false;
            }

            if (!certChainUrl.Contains("/echo.api/echo-api-cert"))
            {
                return false;
            }

            string portTest = certChainUrl.ToLower().Split(':', '/')[1];
            if (!String.IsNullOrEmpty(portTest))
            {
                if (portTest != "443")
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Gets the cache key to use for a certificate chain URL.
        /// </summary>
        /// <param name="certChainUrl">The URL of the cert being validated.</param>
        /// <returns>The unique cache key.</returns>
        private static string GetCacheKey(string certChainUrl)
        {
            return CERT_CACHE_KEY_PREFIX + (certChainUrl ?? string.Empty);
        }
    }
}