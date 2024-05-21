pipeline {
    agent any

    stages {
        stage('Checkout') {
            steps {
                // Checkout the code from the repository
                checkout scm
            }
        }
        stage('Restore') {
            steps {
                // Restore NuGet packages
                script {
                    sh 'dotnet restore'
                }
            }
        }
        stage('Build') {
            steps {
                // Build the .NET project
                script {
                    sh 'dotnet build --configuration Release'
                }
            }
        }
        stage('Test') {
            steps {
                // Run tests
                script {
                    sh 'dotnet test --configuration Release --no-build'
                }
            }
        }
    }
}
